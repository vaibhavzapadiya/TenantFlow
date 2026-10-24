using TenantFlow.IdentityAccess.Domain.Memberships;

namespace TenantFlow.IdentityAccess.Application.Abstractions;

/// <summary>
/// Persistence port of the IdentityAccess module (ADR-0013): specific to its use cases, not a generic repository.
/// </summary>
public interface IMembershipStore
{
    /// <summary>
    /// Starts a change to one tenant's memberships. Changes to the same tenant are serialized from here until the
    /// returned object is committed or disposed, so reading a count and acting on it is safe (SPEC-0002 criterion 20).
    /// </summary>
    Task<IMembershipChange> BeginChangeAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>The active membership of one identity in one tenant, or null. A read; it takes no lock.</summary>
    Task<Membership?> FindActiveAsync(TenantId tenantId, string issuer, string subject, CancellationToken cancellationToken);

    /// <summary>The tenant's active memberships in a stable order. A read; it takes no lock.</summary>
    Task<IReadOnlyList<Membership>> ListActiveInTenantAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Active memberships of one identity (issuer plus subject, both matched exactly) across all tenants, in a stable
    /// order. A read; it takes no lock.
    /// </summary>
    Task<IReadOnlyList<Membership>> ListActiveForIdentityAsync(string issuer, string subject, CancellationToken cancellationToken);
}
