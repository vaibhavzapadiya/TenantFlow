namespace TenantFlow.IdentityAccess.Contracts;

/// <summary>
/// The permissions a tenant route can require (SPEC-0002 and SPEC-0003 Contract). Names are a stable contract, defined
/// once here so the host and every module that checks <see cref="ITenantContext.HasPermission"/> use the same value.
/// Which role grants which permission is IdentityAccess's own rule (<c>RolePermissions</c>).
/// </summary>
public static class TenantPermissions
{
    public const string TenantRead = "tenant.read";
    public const string MembersRead = "members.read";
    public const string MembersManage = "members.manage";
    public const string CatalogRead = "catalog.read";
    public const string CatalogManage = "catalog.manage";
    public const string InventoryRead = "inventory.read";
    public const string InventoryManage = "inventory.manage";

    /// <summary>Create, read, change, submit and cancel one's own orders (SPEC-0004).</summary>
    public const string OrdersPlace = "orders.place";

    /// <summary>Read and cancel every order of the tenant. Checked by the Orders module per order, not by a route.</summary>
    public const string OrdersManage = "orders.manage";

    /// <summary>Create plans, and read and cancel every subscription of the tenant (SPEC-0007).</summary>
    public const string SubscriptionsManage = "subscriptions.manage";

    /// <summary>List plans, and subscribe to, read and cancel one's own subscriptions (SPEC-0007).</summary>
    public const string SubscriptionsSubscribe = "subscriptions.subscribe";

    /// <summary>Read the tenant's audit log (SPEC-0009 criterion 4).</summary>
    public const string AuditRead = "audit.read";
}
