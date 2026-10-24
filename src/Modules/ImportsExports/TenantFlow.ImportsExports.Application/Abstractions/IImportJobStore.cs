using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.Catalog.Contracts;
using TenantFlow.ImportsExports.Application.ProductImports;
using TenantFlow.ImportsExports.Domain.Files;
using TenantFlow.ImportsExports.Domain.Jobs;

namespace TenantFlow.ImportsExports.Application.Abstractions;

/// <summary>
/// Storage of import jobs. Every method acts on the tenant in the current tenant context and runs in a transaction that
/// sets it for row-level security (ADR-0011).
/// </summary>
public interface IImportJobStore
{
    Task<ImportJob?> FindAsync(Guid jobId, CancellationToken cancellationToken);

    Task<ImportJob?> FindByKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction (ADR-0024 decision 3): when fewer than <paramref name="maxActive"/> jobs are active (updated within
    /// <see cref="ImportLimits.StaleAfter"/>), inserts the
    /// file row, the job (<c>receiving</c>), its abandon due row and <paramref name="audit"/> in the outbox; nothing is
    /// written when the key is taken or the tenant is at its limit.
    /// </summary>
    Task<JobStart> TryStartAsync(ImportJob job, StoredFile file, int maxActive, DateTimeOffset abandonAt, OutgoingMessage audit, CancellationToken cancellationToken);

    /// <summary>Moves a receiving job to <c>queued</c> and schedules its run at once, in one transaction.</summary>
    Task<bool> QueueAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Moves a queued job to <c>running</c>; true when it is (now) running.</summary>
    Task<bool> StartRunningAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Records an applied batch only if the checkpoint is still <paramref name="expectedCheckpoint"/>: moves it to
    /// <paramref name="newCheckpoint"/>, adds the counts and stores the row errors (each once).
    /// </summary>
    Task<bool> CheckpointAsync(Guid jobId, int expectedCheckpoint, int newCheckpoint, ImportBatchResult result, IReadOnlyList<ImportRowError> readErrors, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Ends a job that is not final yet (or, with <paramref name="onlyFrom"/>, only one in that status) with
    /// <paramref name="status"/> and a failure code when it failed; true when this call ended it.
    /// </summary>
    Task<bool> FinishAsync(Guid jobId, JobStatus status, string? failureCode, DateTimeOffset now, CancellationToken cancellationToken, JobStatus? onlyFrom = null);

    Task<IReadOnlyList<ImportRowError>> ListErrorsAsync(Guid jobId, int limit, CancellationToken cancellationToken);
}

/// <summary>What <see cref="IImportJobStore.TryStartAsync"/> did.</summary>
public enum JobStart
{
    Started = 1,

    KeyTaken = 2,

    TooManyJobs = 3,
}
