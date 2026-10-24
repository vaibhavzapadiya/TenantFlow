using Microsoft.EntityFrameworkCore;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.BuildingBlocks.Messaging.Outbox;
using TenantFlow.BuildingBlocks.Messaging.Timers;
using TenantFlow.BuildingBlocks.Persistence;
using TenantFlow.Catalog.Contracts;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.ImportsExports.Application.Abstractions;
using TenantFlow.ImportsExports.Application.Files;
using TenantFlow.ImportsExports.Application.ProductImports;
using TenantFlow.ImportsExports.Domain.Files;
using TenantFlow.ImportsExports.Domain.Jobs;

namespace TenantFlow.ImportsExports.Infrastructure.Persistence;

/// <summary>
/// Every operation filters by the tenant context's id and runs in a transaction that sets it for row-level security
/// (ADR-0011). State changes are conditional updates on the expected state, so racing runs cannot both win.
/// </summary>
internal sealed class ImportJobStore(ImportsExportsDbContext db, ITenantContext tenant) : IImportJobStore
{
    private static readonly string ScheduleTimerSql = WorkflowTimerSql.Schedule(ImportsExportsDbContext.Schema);

    public async Task<ImportJob?> FindAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        return await db.ImportJobs.AsNoTracking().SingleOrDefaultAsync(j => j.TenantId == tenantId && j.Id == jobId, cancellationToken);
    }

    public async Task<ImportJob?> FindByKeyAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        return await db.ImportJobs.AsNoTracking().SingleOrDefaultAsync(j => j.TenantId == tenantId && j.IdempotencyKey == idempotencyKey, cancellationToken);
    }

    public async Task<JobStart> TryStartAsync(ImportJob job, StoredFile file, int maxActive, DateTimeOffset abandonAt, OutgoingMessage audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(file);
        var tenantId = tenant.TenantId;
        if (job.TenantId != tenantId || file.TenantId != tenantId)
        {
            throw new InvalidOperationException("An import can only be started in the tenant in the current context.");
        }

        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        // Serializes submissions of one tenant, so two cannot both pass the active-job count (ADR-0018 advisory locks).
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"imports:" + tenantId.ToString("N")}, 0))", cancellationToken);
        if (await db.ImportJobs.AnyAsync(j => j.TenantId == tenantId && j.IdempotencyKey == job.IdempotencyKey, cancellationToken))
        {
            return JobStart.KeyTaken;
        }

        var staleBefore = job.CreatedAt - ImportLimits.StaleAfter;
        var active = await db.ImportJobs.CountAsync(
            j => j.TenantId == tenantId && (j.Status == JobStatus.Receiving || j.Status == JobStatus.Queued || j.Status == JobStatus.Running) && j.UpdatedAt > staleBefore,
            cancellationToken);
        if (active >= maxActive)
        {
            return JobStart.TooManyJobs;
        }

        db.Files.Add(file);
        db.ImportJobs.Add(job);
        // The audit event exists if and only if the request committed (ADR-0026).
        db.OutboxMessages.Add(OutboxMessage.Create(audit, tenantId, job.CreatedAt));
        await db.SaveChangesAsync(cancellationToken);
        // An upload that never arrives is failed later (SPEC-0008 criterion 10); the row exists if and only if the job does.
        await db.Database.ExecuteSqlRawAsync(ScheduleTimerSql, [Guid.CreateVersion7(job.CreatedAt), tenantId, ImportLimits.AbandonKind, job.Id, abandonAt], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return JobStart.Started;
    }

    public async Task<bool> QueueAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        var changed = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE imports_exports.import_jobs SET status = {JobStatusNames.Queued}, updated_at = {now}
            WHERE tenant_id = {tenantId} AND id = {jobId} AND status = {JobStatusNames.Receiving}
            """,
            cancellationToken);
        if (changed == 1)
        {
            await db.Database.ExecuteSqlRawAsync(ScheduleTimerSql, [Guid.CreateVersion7(now), tenantId, ImportLimits.RunKind, jobId, now], cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return changed == 1;
    }

    public async Task<bool> StartRunningAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        var running = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE imports_exports.import_jobs SET status = {JobStatusNames.Running}, updated_at = {now}
            WHERE tenant_id = {tenantId} AND id = {jobId} AND status IN ({JobStatusNames.Queued}, {JobStatusNames.Running})
            """,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return running == 1;
    }

    public async Task<bool> CheckpointAsync(Guid jobId, int expectedCheckpoint, int newCheckpoint, ImportBatchResult result, IReadOnlyList<ImportRowError> readErrors, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(readErrors);
        var tenantId = tenant.TenantId;
        var errors = result.Errors.Concat(readErrors).ToList();
        var failedRows = errors.Select(static e => e.Row).Distinct().Count();
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        var moved = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE imports_exports.import_jobs
            SET checkpoint_row = {newCheckpoint}, created_count = created_count + {result.Created}, updated_count = updated_count + {result.Updated},
                failed_count = failed_count + {failedRows}, updated_at = {now}
            WHERE tenant_id = {tenantId} AND id = {jobId} AND status = {JobStatusNames.Running} AND checkpoint_row = {expectedCheckpoint}
            """,
            cancellationToken);
        if (moved == 1)
        {
            foreach (var error in errors)
            {
                await db.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO imports_exports.import_row_errors (tenant_id, job_id, "row", "column", code)
                    VALUES ({tenantId}, {jobId}, {error.Row}, {error.Column}, {error.Code})
                    ON CONFLICT DO NOTHING
                    """,
                    cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return moved == 1;
    }

    public async Task<bool> FinishAsync(Guid jobId, JobStatus status, string? failureCode, DateTimeOffset now, CancellationToken cancellationToken, JobStatus? onlyFrom = null)
    {
        var tenantId = tenant.TenantId;
        string[] from = onlyFrom is { } only ? [only.ToName()] : [.. JobStatusNames.Active];
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        var finished = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE imports_exports.import_jobs SET status = {status.ToName()}, failure_code = {failureCode}, updated_at = {now}, completed_at = {now}
            WHERE tenant_id = {tenantId} AND id = {jobId} AND status = ANY({from})
            """,
            cancellationToken);
        if (finished == 1)
        {
            // The upload is kept for a while after the import ended, then deleted (SPEC-0008 criterion 10).
            var expiresAt = now + ImportLimits.UploadRetention;
            var fileId = await db.ImportJobs.Where(j => j.TenantId == tenantId && j.Id == jobId).Select(static j => j.FileId).SingleAsync(cancellationToken);
            await db.Database.ExecuteSqlAsync(
                $"UPDATE imports_exports.stored_files SET expires_at = {expiresAt} WHERE tenant_id = {tenantId} AND id = {fileId}",
                cancellationToken);
            await db.Database.ExecuteSqlRawAsync(ScheduleTimerSql, [Guid.CreateVersion7(now), tenantId, FileExpiry.Kind, fileId, expiresAt], cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return finished == 1;
    }

    public async Task<IReadOnlyList<ImportRowError>> ListErrorsAsync(Guid jobId, int limit, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        return await db.ImportRowErrors.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.JobId == jobId)
            .OrderBy(static e => e.Row).ThenBy(static e => e.Column)
            .Take(limit)
            .Select(static e => new ImportRowError(e.Row, e.Column, e.Code))
            .ToListAsync(cancellationToken);
    }
}
