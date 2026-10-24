using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.ImportsExports.Domain.Files;
using TenantFlow.ImportsExports.Domain.Jobs;

namespace TenantFlow.ImportsExports.Application.Abstractions;

/// <summary>
/// Storage of export jobs. Every method acts on the tenant in the current tenant context and runs in a transaction that
/// sets it for row-level security (ADR-0011).
/// </summary>
public interface IExportJobStore
{
    Task<ExportJob?> FindAsync(Guid jobId, CancellationToken cancellationToken);

    Task<ExportJob?> FindByKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction: when fewer than <paramref name="maxActive"/> exports are active, inserts the file row, the job
    /// (<c>queued</c>), its run due row and <paramref name="audit"/> in the outbox; nothing is written when the key is taken
    /// or the tenant is at its limit.
    /// </summary>
    Task<JobStart> TryStartAsync(ExportJob job, StoredFile file, int maxActive, OutgoingMessage audit, CancellationToken cancellationToken);

    /// <summary>Commits <paramref name="message"/> to the module's outbox in the current tenant (ADR-0021).</summary>
    Task AnnounceAsync(OutgoingMessage message, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Moves a queued job to <c>running</c>; true when it is (now) running.</summary>
    Task<bool> StartRunningAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction, only while the job is running: records the written file's size and checksum and moves the job to
    /// <c>succeeded</c> with its row count and expiry.
    /// </summary>
    Task<bool> CompleteAsync(Guid jobId, StoredObject written, int rowCount, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Ends a job that is not final yet as <c>failed</c> with <paramref name="failureCode"/>.</summary>
    Task<bool> FailAsync(Guid jobId, string failureCode, DateTimeOffset now, CancellationToken cancellationToken);
}
