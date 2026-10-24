using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.IdentityAccess.Domain.Auditing;
using TenantFlow.IdentityAccess.Domain.Memberships;

namespace TenantFlow.IdentityAccess.Application.Abstractions;

/// <summary>
/// One serialized change to one tenant's memberships. Every read is limited to that tenant, so a membership id that
/// belongs to another tenant is simply not found (SPEC-0002 criterion 17). Disposing without
/// <see cref="CommitAsync"/> discards everything.
/// </summary>
public interface IMembershipChange : IAsyncDisposable
{
    /// <summary>The active membership with this id in this tenant, or null.</summary>
    Task<Membership?> FindActiveAsync(MembershipId id, CancellationToken cancellationToken);

    /// <summary>The active membership of this identity in this tenant, or null.</summary>
    Task<Membership?> FindActiveAsync(string issuer, string subject, CancellationToken cancellationToken);

    Task<int> CountActiveAdministratorsAsync(CancellationToken cancellationToken);

    void Add(Membership membership);

    void Append(MembershipAuditRecord record);

    /// <summary>
    /// Writes <paramref name="message"/> to the module's outbox for this change's tenant, committed by
    /// <see cref="CommitAsync"/> together with the change it announces (ADR-0021, ADR-0026).
    /// </summary>
    void Announce(OutgoingMessage message, DateTimeOffset occurredAt);

    /// <summary>Writes the changes, their audit records and their outbox messages in one transaction.</summary>
    /// <exception cref="TenantFlow.BuildingBlocks.Abstractions.Errors.AppException">
    /// <c>membership.already_exists</c> if the identity already has an active membership in the tenant.
    /// </exception>
    Task CommitAsync(CancellationToken cancellationToken);
}
