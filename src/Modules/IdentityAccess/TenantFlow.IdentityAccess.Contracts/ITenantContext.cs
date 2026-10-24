namespace TenantFlow.IdentityAccess.Contracts;

/// <summary>
/// Who is acting in which tenant, for the current request. It is set once, from a resolved membership in an active
/// tenant, and only by the IdentityAccess module (ADR-0010; SPEC-0002 criterion 15). Tenant-owned code reads it and
/// never takes a tenant id from a route, header, or body: those select a tenant, they never grant access.
/// </summary>
/// <remarks>Every member except <see cref="IsSet"/> fails closed: with no tenant set they throw.</remarks>
public interface ITenantContext
{
    /// <summary>True once a membership has been resolved for this request. Use it to branch; use the members to act.</summary>
    bool IsSet { get; }

    /// <exception cref="TenantContextNotSetException">No tenant has been resolved for this request.</exception>
    Guid TenantId { get; }

    /// <exception cref="TenantContextNotSetException">No tenant has been resolved for this request.</exception>
    Guid MembershipId { get; }

    /// <summary>The caller's role in the tenant, by its stable name (for example <c>tenant-admin</c>).</summary>
    /// <exception cref="TenantContextNotSetException">No tenant has been resolved for this request.</exception>
    string Role { get; }

    /// <exception cref="TenantContextNotSetException">No tenant has been resolved for this request.</exception>
    bool HasPermission(string permission);
}
