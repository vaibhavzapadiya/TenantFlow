using TenantFlow.IdentityAccess.Contracts;

namespace TenantFlow.IdentityAccess.Domain.Memberships;

/// <summary>
/// Which permissions each role grants (SPEC-0002 Contract). Authority comes from this table and the membership in the
/// database, never from token claims (ADR-0010).
/// </summary>
public static class RolePermissions
{
    private static readonly string[] TenantAdminPermissions = [TenantPermissions.TenantRead, TenantPermissions.MembersRead, TenantPermissions.MembersManage, TenantPermissions.CatalogRead, TenantPermissions.CatalogManage, TenantPermissions.InventoryRead, TenantPermissions.InventoryManage, TenantPermissions.OrdersPlace, TenantPermissions.OrdersManage, TenantPermissions.SubscriptionsManage, TenantPermissions.SubscriptionsSubscribe, TenantPermissions.AuditRead];
    private static readonly string[] CustomerPermissions = [TenantPermissions.TenantRead, TenantPermissions.CatalogRead, TenantPermissions.OrdersPlace, TenantPermissions.SubscriptionsSubscribe];

    /// <exception cref="ArgumentOutOfRangeException">The role is not one the domain defines.</exception>
    public static IReadOnlyCollection<string> For(MembershipRole role) => role switch
    {
        MembershipRole.TenantAdmin => TenantAdminPermissions,
        MembershipRole.Customer => CustomerPermissions,
        _ => throw new ArgumentOutOfRangeException(nameof(role), "The membership role is not one the domain defines."),
    };

    public static bool Has(MembershipRole role, string permission) => For(role).Contains(permission, StringComparer.Ordinal);
}
