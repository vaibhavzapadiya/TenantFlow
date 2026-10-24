using TenantFlow.Audit.Domain.Records;

namespace TenantFlow.Audit.Application.Abstractions;

/// <summary>
/// Storage for the audit log. Like every consumer's store it takes the tenant from the record, which comes from the
/// validated message envelope (ADR-0010 service boundary, ADR-0021); every write runs in a transaction that sets that
/// tenant for row-level security. It can only add records: the log has no update or delete (ADR-0026).
/// </summary>
public interface IAuditStore
{
    /// <summary>
    /// Records <paramref name="record"/> and the processed message for <paramref name="consumer"/> in one transaction.
    /// False, with nothing written, when that consumer already processed the message.
    /// </summary>
    Task<bool> RecordAsync(AuditRecord record, string consumer, CancellationToken cancellationToken);

    /// <summary>
    /// Up to <paramref name="take"/> of <paramref name="tenantId"/>'s records after <paramref name="after"/>, newest first
    /// by (occurred at, id), only of <paramref name="action"/> when given. Read in a transaction scoped to that tenant, so
    /// row-level security returns no other tenant's record whatever the filter says.
    /// </summary>
    Task<IReadOnlyList<AuditRecord>> ListAsync(Guid tenantId, AuditCursor? after, string? action, int take, CancellationToken cancellationToken);
}
