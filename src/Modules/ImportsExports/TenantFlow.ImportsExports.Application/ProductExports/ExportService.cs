using System.Security.Cryptography;
using System.Text;
using TenantFlow.Audit.Contracts;
using TenantFlow.Audit.Contracts.Events;
using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.ImportsExports.Application.Abstractions;
using TenantFlow.ImportsExports.Application.Files;
using TenantFlow.ImportsExports.Domain.Files;
using TenantFlow.ImportsExports.Domain.Jobs;

namespace TenantFlow.ImportsExports.Application.ProductExports;

/// <summary>
/// Submitting, reading and downloading product exports (SPEC-0008 criteria 7 and 9). The route has checked
/// <c>catalog.manage</c>. Submission commits the file row, the queued job and its run due row together; the Worker writes
/// the file. A repeat with the same key by the same member answers the same job.
/// </summary>
public sealed class ExportService(ITenantContext context, IExportJobStore store, StoredFiles files, TimeProvider clock)
{
    // What an export request asks for: always every product, so the key binds to the caller only.
    private const string Request = "products";

    /// <exception cref="AppException">
    /// <see cref="JobErrors.IdempotencyKeyInvalid"/>, <see cref="JobErrors.IdempotencyKeyReused"/> or
    /// <see cref="JobErrors.TooManyJobs"/>.
    /// </exception>
    public async Task<ExportJobDetails> SubmitAsync(string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (ImportJob.CheckIdempotencyKey(idempotencyKey) is { } keyError)
        {
            throw new AppException(keyError);
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{context.MembershipId:D}\n{Request}")));
        if (await AnswerAsync(idempotencyKey!, hash, cancellationToken) is { } replayed)
        {
            return replayed;
        }

        var now = clock.GetUtcNow();
        var file = StoredFile.Reserve(Guid.CreateVersion7(now), context.TenantId, FileKinds.Export, "text/csv", now);
        var job = ExportJob.Queue(Guid.CreateVersion7(now), context.TenantId, file.Id, idempotencyKey!, hash, context.MembershipId, now);
        // Audited as requested by the member, by membership id only (SPEC-0009 criterion 2).
        return await store.TryStartAsync(job, file, ExportLimits.MaxActiveJobs, Audit(AuditActions.ExportRequested, job.Id, now), cancellationToken) switch
        {
            JobStart.KeyTaken => await AnswerAsync(idempotencyKey!, hash, cancellationToken) ?? throw new AppException(JobErrors.IdempotencyKeyReused),
            JobStart.TooManyJobs => throw new AppException(JobErrors.TooManyJobs),
            _ => ExportJobDetails.From(job),
        };
    }

    /// <exception cref="AppException"><see cref="JobErrors.NotFound"/>.</exception>
    public async Task<ExportJobDetails> GetAsync(Guid jobId, CancellationToken cancellationToken) =>
        ExportJobDetails.From(await store.FindAsync(jobId, cancellationToken) ?? throw new AppException(JobErrors.NotFound));

    /// <summary>The export's CSV file, checked against its checksum as it is read.</summary>
    /// <exception cref="AppException">
    /// <see cref="JobErrors.NotFound"/>; <see cref="JobErrors.FileNotReady"/> when the export has not succeeded, its file
    /// expired, or the file is gone; <see cref="JobErrors.StorageUnavailable"/> when storage cannot be reached, so the
    /// caller retries (ADR-0024 amendment 1).
    /// </exception>
    public async Task<Stream> OpenFileAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await store.FindAsync(jobId, cancellationToken) ?? throw new AppException(JobErrors.NotFound);
        var now = clock.GetUtcNow();
        var stream = job.IsDownloadable(now)
            ? await OpenStoredAsync(job.FileId, cancellationToken) ?? throw new AppException(JobErrors.FileNotReady)
            : throw new AppException(JobErrors.FileNotReady);
        try
        {
            // The download is audited before a byte is served: no record, no file (SPEC-0009 criterion 2).
            await store.AnnounceAsync(Audit(AuditActions.ExportDownloaded, job.Id, now), now, cancellationToken);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    // The provider's detail stays the inner exception; the caller gets only the fixed message.
    private async Task<Stream?> OpenStoredAsync(Guid fileId, CancellationToken cancellationToken)
    {
        try
        {
            return await files.OpenAsync(fileId, cancellationToken);
        }
        catch (StorageUnavailableException exception)
        {
            throw new AppException(JobErrors.StorageUnavailable, exception);
        }
    }

    private OutgoingMessage Audit(string action, Guid jobId, DateTimeOffset now) =>
        OutgoingMessage.From(AuditRecordedV1.Of(action, AuditActor.Member(context.MembershipId), AuditTargetTypes.ExportJob, jobId, now));

    // The job a key already started when the caller is the same; another caller with the key is refused.
    private async Task<ExportJobDetails?> AnswerAsync(string key, string hash, CancellationToken cancellationToken)
    {
        var existing = await store.FindByKeyAsync(key, cancellationToken);
        return existing is null
            ? null
            : string.Equals(existing.RequestHash, hash, StringComparison.Ordinal)
                ? ExportJobDetails.From(existing)
                : throw new AppException(JobErrors.IdempotencyKeyReused);
    }
}
