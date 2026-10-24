using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.Operations.Domain.SupportGrants;

namespace TenantFlow.Operations.Application.Abstractions;

/// <summary>
/// Storage of support grants (ADR-0027). Each write commits its audit event to the module's outbox in the same
/// transaction (ADR-0026), so a grant, an end or an access is recorded if and only if it happened.
/// </summary>
public interface ISupportGrantStore
{
    /// <summary>
    /// Serialized per operator and tenant: false, with nothing written, when the operator already has an active grant for
    /// the tenant at <paramref name="now"/>; otherwise the grant and <paramref name="audit"/>.
    /// </summary>
    Task<bool> TryOpenAsync(SupportGrant grant, OutgoingMessage audit, DateTimeOffset now, CancellationToken cancellationToken);

    Task<SupportGrant?> FindAsync(Guid grantId, CancellationToken cancellationToken);

    /// <summary>Ends the grant if it is still active, with its audit event; false, with nothing written, otherwise.</summary>
    Task<bool> EndAsync(Guid grantId, string endedBy, DateTimeOffset now, OutgoingMessage audit, CancellationToken cancellationToken);

    /// <summary>
    /// The operator's active grant for the tenant at <paramref name="now"/>, with the audit event of this access committed
    /// in the same transaction (SPEC-0009 criterion 17); null, with nothing written, when there is none.
    /// </summary>
    Task<SupportGrant?> RecordAccessAsync(Guid tenantId, string operatorRef, DateTimeOffset now, Func<SupportGrant, OutgoingMessage> audit, CancellationToken cancellationToken);
}
