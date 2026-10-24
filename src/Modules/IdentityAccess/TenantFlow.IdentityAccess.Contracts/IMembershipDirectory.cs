namespace TenantFlow.IdentityAccess.Contracts;

/// <summary>
/// What other modules may ask about memberships of the tenant in the current tenant context (ADR-0002): whether one is
/// still active. Background work that acts for a member, such as a subscription renewal, asks it before acting
/// (SPEC-0007 review fix R9).
/// </summary>
public interface IMembershipDirectory
{
    /// <summary>True when the membership exists in the current tenant and has not been removed.</summary>
    /// <exception cref="TenantContextNotSetException">No tenant has been set for this scope.</exception>
    Task<bool> IsActiveAsync(Guid membershipId, CancellationToken cancellationToken);

    /// <summary>
    /// True when the membership is active in the current tenant and its role grants <paramref name="permission"/> now
    /// (SPEC-0009 criterion 11): background work that acts for a member asks it before each step that writes.
    /// </summary>
    /// <exception cref="TenantContextNotSetException">No tenant has been set for this scope.</exception>
    Task<bool> HasPermissionAsync(Guid membershipId, string permission, CancellationToken cancellationToken);
}
