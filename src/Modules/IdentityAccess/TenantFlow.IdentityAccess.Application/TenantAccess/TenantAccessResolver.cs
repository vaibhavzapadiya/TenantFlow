using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Abstractions.Identity;
using TenantFlow.IdentityAccess.Application.Abstractions;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.IdentityAccess.Domain.Memberships;
using TenantFlow.Tenants.Contracts;

namespace TenantFlow.IdentityAccess.Application.TenantAccess;

/// <summary>
/// Turns "this authenticated caller names this tenant" into either a tenant context or a denial (ADR-0010; SPEC-0002
/// criteria 12 to 15). The tenant id in the route only selects a tenant; authority comes from an active membership in
/// an active tenant, read from the database on every request, never from token claims. The resolver is also the tenant
/// context for its scope: it is the only place that can set it, it sets it once, and only after every check passed.
/// </summary>
/// <remarks>
/// Order matters, because each denial must reveal as little as possible:
/// 1. No membership, or no such tenant: <c>tenant.not_found</c> (404), identical to an id that does not exist, so a
///    non-member learns nothing, not even whether the tenant is suspended.
/// 2. A member of a suspended tenant: <c>membership.tenant_suspended</c> (403). Only members ever see this.
/// 3. A member whose role lacks the permission: <c>membership.permission_denied</c> (403).
/// </remarks>
public sealed class TenantAccessResolver(IMembershipStore store, ITenantDirectory tenants) : ITenantContext
{
    private Resolved? _resolved;

    public bool IsSet => _resolved is not null;

    public Guid TenantId => Current.TenantId;

    public Guid MembershipId => Current.MembershipId;

    public string Role => Current.Role.ToName();

    public bool HasPermission(string permission) => RolePermissions.Has(Current.Role, permission);

    private Resolved Current => _resolved ?? throw new TenantContextNotSetException();

    /// <summary>
    /// Resolves the caller's membership in the named tenant, checks the tenant is active and the role grants
    /// <paramref name="permission"/>, then sets the tenant context for the rest of this request.
    /// </summary>
    /// <exception cref="AppException">
    /// <see cref="TenantContractErrors.NotFound"/>, <see cref="MembershipErrors.TenantSuspended"/>, or
    /// <see cref="MembershipErrors.PermissionDenied"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">A tenant context was already set for this request.</exception>
    public async Task ResolveAsync(Guid tenantGuid, ActorIdentity caller, string permission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        if (_resolved is not null)
        {
            // Set once per request: a second resolution could swap the tenant under code that already ran.
            throw new InvalidOperationException("The tenant context is already set for this request.");
        }

        if (tenantGuid == Guid.Empty)
        {
            throw new AppException(TenantContractErrors.NotFound);
        }

        var tenantId = new TenantId(tenantGuid);
        var membership = await store.FindActiveAsync(tenantId, caller.Issuer, caller.Subject, cancellationToken)
            ?? throw new AppException(TenantContractErrors.NotFound);

        var availability = await tenants.GetAvailabilityAsync([tenantGuid], cancellationToken);
        if (!availability.TryGetValue(tenantGuid, out var state))
        {
            // A membership of a tenant that no longer exists is not a membership.
            throw new AppException(TenantContractErrors.NotFound);
        }

        if (state != TenantAvailability.Active)
        {
            throw new AppException(MembershipErrors.TenantSuspended);
        }

        if (!RolePermissions.Has(membership.Role, permission))
        {
            throw new AppException(MembershipErrors.PermissionDenied);
        }

        _resolved = new Resolved(tenantGuid, membership.Id.Value, membership.Role);
    }

    private sealed record Resolved(Guid TenantId, Guid MembershipId, MembershipRole Role);
}
