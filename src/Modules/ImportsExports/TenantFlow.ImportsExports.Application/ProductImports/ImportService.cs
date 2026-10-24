using System.Security.Cryptography;
using System.Text;
using TenantFlow.Audit.Contracts;
using TenantFlow.Audit.Contracts.Events;
using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.Catalog.Contracts;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.ImportsExports.Application.Abstractions;
using TenantFlow.ImportsExports.Application.Files;
using TenantFlow.ImportsExports.Domain.Files;
using TenantFlow.ImportsExports.Domain.Jobs;

namespace TenantFlow.ImportsExports.Application.ProductImports;

/// <summary>
/// Submitting and reading product imports (SPEC-0008 criteria 3 and 6). The route has checked <c>catalog.manage</c>.
/// Submission is intent first (ADR-0024 decision 3): the file row, the job (<c>receiving</c>) and its abandon due row
/// commit together; then the upload is written to storage, outside any transaction; then the job is queued with its run
/// due row. A repeat with the same key and the same file answers the same job.
/// </summary>
public sealed class ImportService(ITenantContext context, IImportJobStore store, StoredFiles files, TimeProvider clock)
{
    private static readonly string[] CsvContentTypes = ["text/csv", "application/csv"];

    /// <exception cref="AppException">
    /// <see cref="JobErrors.IdempotencyKeyInvalid"/>, <see cref="JobErrors.FileNotCsv"/>, <see cref="JobErrors.FileTooLarge"/>,
    /// <see cref="JobErrors.IdempotencyKeyReused"/> or <see cref="JobErrors.TooManyJobs"/>.
    /// </exception>
    public async Task<ImportJobDetails> SubmitAsync(Stream content, string? contentType, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        CheckIdempotencyKey(idempotencyKey);

        if (contentType is null || !CsvContentTypes.Contains(contentType.Split(';')[0].Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new AppException(JobErrors.FileNotCsv);
        }

        // At most 5 MB, held in memory to hash it before anything is written.
        var upload = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (upload.Length + read > ImportLimits.MaxBytes)
            {
                throw new AppException(JobErrors.FileTooLarge);
            }

            await upload.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        if (upload.Length == 0)
        {
            throw new AppException(JobErrors.FileNotCsv);
        }

        var hash = RequestHash(SHA256.HashData(upload.ToArray()));
        if (await AnswerAsync(idempotencyKey!, hash, upload, cancellationToken) is { } replayed)
        {
            return replayed;
        }

        var now = clock.GetUtcNow();
        var file = StoredFile.Reserve(Guid.CreateVersion7(now), context.TenantId, FileKinds.ImportUpload, "text/csv", now);
        var job = ImportJob.Receive(Guid.CreateVersion7(now), context.TenantId, file.Id, idempotencyKey!, hash, context.MembershipId, now);
        // Audited as requested by the member, by membership id only (SPEC-0009 criterion 2).
        var audit = OutgoingMessage.From(AuditRecordedV1.Of(AuditActions.ImportRequested, AuditActor.Member(context.MembershipId), AuditTargetTypes.ImportJob, job.Id, now));
        switch (await store.TryStartAsync(job, file, ImportLimits.MaxActiveJobs, now + ImportLimits.AbandonAfter, audit, cancellationToken))
        {
            case JobStart.KeyTaken:
                return await AnswerAsync(idempotencyKey!, hash, upload, cancellationToken) ?? throw new AppException(JobErrors.IdempotencyKeyReused);
            case JobStart.TooManyJobs:
                throw new AppException(JobErrors.TooManyJobs);
        }

        // Committed: from here on finish regardless of the client.
        var committed = CancellationToken.None;
        upload.Position = 0;
        await files.WriteAsync(file, upload, ImportLimits.MaxBytes, committed);
        await store.QueueAsync(job.Id, clock.GetUtcNow(), committed);
        return ImportJobDetails.From((await store.FindAsync(job.Id, committed))!);
    }

    /// <summary>Refuses a missing or malformed key before anything else is read.</summary>
    /// <exception cref="AppException"><see cref="JobErrors.IdempotencyKeyInvalid"/>.</exception>
    public static void CheckIdempotencyKey(string? idempotencyKey)
    {
        if (ImportJob.CheckIdempotencyKey(idempotencyKey) is { } keyError)
        {
            throw new AppException(keyError);
        }
    }

    /// <exception cref="AppException"><see cref="JobErrors.NotFound"/>.</exception>
    public async Task<ImportJobDetails> GetAsync(Guid jobId, CancellationToken cancellationToken) =>
        ImportJobDetails.From(await store.FindAsync(jobId, cancellationToken) ?? throw new AppException(JobErrors.NotFound));

    /// <summary>The job's row errors, by row: where and why, never a value (first <see cref="ImportLimits.MaxListedErrors"/>).</summary>
    /// <exception cref="AppException"><see cref="JobErrors.NotFound"/>.</exception>
    public async Task<IReadOnlyList<ImportRowError>> ErrorsAsync(Guid jobId, CancellationToken cancellationToken)
    {
        _ = await store.FindAsync(jobId, cancellationToken) ?? throw new AppException(JobErrors.NotFound);
        return await store.ListErrorsAsync(jobId, ImportLimits.MaxListedErrors, cancellationToken);
    }

    // The job a key already started when the request is the same; a different request with the key is refused. A job still
    // receiving lost its upload to a failure after its intent committed: the same request (the hash proves the same file)
    // writes the upload again and queues it, so a client's retry after an error succeeds instead of being abandoned.
    private async Task<ImportJobDetails?> AnswerAsync(string key, string hash, MemoryStream upload, CancellationToken cancellationToken)
    {
        var existing = await store.FindByKeyAsync(key, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        if (!string.Equals(existing.RequestHash, hash, StringComparison.Ordinal))
        {
            throw new AppException(JobErrors.IdempotencyKeyReused);
        }

        if (existing.Status != JobStatus.Receiving)
        {
            return ImportJobDetails.From(existing);
        }

        var committed = CancellationToken.None;
        upload.Position = 0;
        await files.WriteAsync(existing.FileId, upload, ImportLimits.MaxBytes, committed);
        await store.QueueAsync(existing.Id, clock.GetUtcNow(), committed);
        return ImportJobDetails.From((await store.FindAsync(existing.Id, committed))!);
    }

    // The caller's membership and the file's content: what the key promises to mean.
    private string RequestHash(byte[] contentSha256) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{context.MembershipId:D}\n{Convert.ToHexStringLower(contentSha256)}")));
}
