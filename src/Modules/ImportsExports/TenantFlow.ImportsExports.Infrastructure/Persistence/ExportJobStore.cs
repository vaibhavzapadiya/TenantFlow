using Microsoft.EntityFrameworkCore;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.BuildingBlocks.Messaging.Outbox;
using TenantFlow.BuildingBlocks.Messaging.Timers;
using TenantFlow.BuildingBlocks.Persistence;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.ImportsExports.Application.Abstractions;
using TenantFlow.ImportsExports.Application.Files;
using TenantFlow.ImportsExports.Application.ProductExports;
using TenantFlow.ImportsExports.Application.ProductImports;
using TenantFlow.ImportsExports.Domain.Files;
using TenantFlow.ImportsExports.Domain.Jobs;

namespace TenantFlow.ImportsExports.Infrastructure.Persistence;

/// <summary>
/// Every operation filters by the tenant context's id and runs in a transaction that sets it for row-level security
/// (ADR-0011). State changes are conditional updates on the expected state, so racing runs cannot both win.
/// </summary>
internal sealed class ExportJobStore(ImportsExportsDbContext db, ITenantContext tenant) : IExportJobStore
{
    private static readonly string ScheduleTimerSql = WorkflowTimerSql.Schedule(ImportsExportsDbContext.Schema);

    public async Task<ExportJob?> FindAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        return await db.ExportJobs.AsNoTracking().SingleOrDefaultAsync(j => j.TenantId == tenantId && j.Id == jobId, cancellationToken);
    }

    public async Task<ExportJob?> FindByKeyAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        return await db.ExportJobs.AsNoTracking().SingleOrDefaultAsync(j => j.TenantId == tenantId && j.IdempotencyKey == idempotencyKey, cancellationToken);
    }

    public async Task<JobStart> TryStartAsync(ExportJob job, StoredFile file, int maxActive, OutgoingMessage audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(file);
        var tenantId = tenant.TenantId;
        if (job.TenantId != tenantId || file.TenantId != tenantId)
        {
            throw new InvalidOperationException("An export can only be started in the tenant in the current context.");
        }

        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        // Serializes submissions of one tenant, so two cannot both pass the active-job count (ADR-0018 advisory locks).
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"exports:" + tenantId.ToString("N")}, 0))", cancellationToken);
        if (await db.ExportJobs.AnyAsync(j => j.TenantId == tenantId && j.IdempotencyKey == job.IdempotencyKey, cancellationToken))
        {
            return JobStart.KeyTaken;
        }

        var staleBefore = job.CreatedAt - ImportLimits.StaleAfter;
        var active = await db.ExportJobs.CountAsync(
            j => j.TenantId == tenantId && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running) && j.UpdatedAt > staleBefore, cancellationToken);
        if (active >= maxActive)
        {
            return JobStart.TooManyJobs;
        }

        db.Files.Add(file);
        db.ExportJobs.Add(job);
        // The audit event exists if and only if the request committed (ADR-0026).
        db.OutboxMessages.Add(OutboxMessage.Create(audit, tenantId, job.CreatedAt));
        await db.SaveChangesAsync(cancellationToken);
        // The run due row exists if and only if the job does (ADR-0024 decision 3).
        await db.Database.ExecuteSqlRawAsync(ScheduleTimerSql, [Guid.CreateVersion7(job.CreatedAt), tenantId, ExportLimits.RunKind, job.Id, job.CreatedAt], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return JobStart.Started;
    }

    public async Task AnnounceAsync(OutgoingMessage message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        db.OutboxMessages.Add(OutboxMessage.Create(message, tenantId, now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    public async Task<bool> StartRunningAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        var running = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE imports_exports.export_jobs SET status = {JobStatusNames.Running}, updated_at = {now}
            WHERE tenant_id = {tenantId} AND id = {jobId} AND status IN ({JobStatusNames.Queued}, {JobStatusNames.Running})
            """,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return running == 1;
    }

    public async Task<bool> CompleteAsync(Guid jobId, StoredObject written, int rowCount, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(written);
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        var completed = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE imports_exports.export_jobs
            SET status = {JobStatusNames.Succeeded}, row_count = {rowCount}, expires_at = {expiresAt}, updated_at = {now}, completed_at = {now}
            WHERE tenant_id = {tenantId} AND id = {jobId} AND status = {JobStatusNames.Running}
            """,
            cancellationToken);
        if (completed == 1)
        {
            var fileId = await db.ExportJobs.Where(j => j.TenantId == tenantId && j.Id == jobId).Select(static j => j.FileId).SingleAsync(cancellationToken);
            await db.Database.ExecuteSqlAsync(
                $"UPDATE imports_exports.stored_files SET size = {written.Size}, sha256 = {written.Sha256}, expires_at = {expiresAt} WHERE tenant_id = {tenantId} AND id = {fileId}",
                cancellationToken);
            // The file is deleted when it expires (SPEC-0008 criterion 10).
            await db.Database.ExecuteSqlRawAsync(ScheduleTimerSql, [Guid.CreateVersion7(now), tenantId, FileExpiry.Kind, fileId, expiresAt], cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return completed == 1;
    }

    public async Task<bool> FailAsync(Guid jobId, string failureCode, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        var failed = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE imports_exports.export_jobs SET status = {JobStatusNames.Failed}, failure_code = {failureCode}, updated_at = {now}, completed_at = {now}
            WHERE tenant_id = {tenantId} AND id = {jobId} AND status IN ({JobStatusNames.Queued}, {JobStatusNames.Running})
            """,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return failed == 1;
    }
}
