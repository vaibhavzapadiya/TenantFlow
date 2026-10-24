using TenantFlow.ImportsExports.Domain.Files;

namespace TenantFlow.ImportsExports.Application.Abstractions;

/// <summary>
/// Storage of file rows. Every method acts on the tenant in the current tenant context: it takes no tenant id, filters by
/// it, and runs in a transaction that sets it for row-level security (ADR-0011). With no tenant set it throws first.
/// </summary>
public interface IFileStore
{
    Task AddAsync(StoredFile file, CancellationToken cancellationToken);

    Task<StoredFile?> FindAsync(Guid fileId, CancellationToken cancellationToken);

    /// <summary>Records what was written: size and checksum.</summary>
    Task MarkWrittenAsync(Guid fileId, StoredObject written, CancellationToken cancellationToken);

    /// <summary>Records that the object was removed.</summary>
    Task MarkDeletedAsync(Guid fileId, DateTimeOffset now, CancellationToken cancellationToken);
}
