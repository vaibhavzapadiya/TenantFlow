using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.IdentityAccess.Domain.Memberships;

namespace TenantFlow.IdentityAccess.Tests.Domain;

/// <summary>SPEC-0002 Contract: roles map to permissions, and the role names are one strict, stable definition.</summary>
public sealed class RolePermissionsTests
{
    [Fact]
    public void Tenant_admin_holds_exactly_its_permissions()
    {
        Assert.Equal(
            ["audit.read", "catalog.manage", "catalog.read", "inventory.manage", "inventory.read", "members.manage", "members.read", "orders.manage", "orders.place", "subscriptions.manage", "subscriptions.subscribe", "tenant.read"],
            RolePermissions.For(MembershipRole.TenantAdmin).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Customer_can_read_the_tenant_and_the_catalog_place_orders_and_subscribe_and_nothing_else()
    {
        Assert.Equal(["catalog.read", "orders.place", "subscriptions.subscribe", "tenant.read"], RolePermissions.For(MembershipRole.Customer).Order(StringComparer.Ordinal).ToArray());
        Assert.False(RolePermissions.Has(MembershipRole.Customer, TenantPermissions.CatalogManage));
        Assert.False(RolePermissions.Has(MembershipRole.Customer, TenantPermissions.MembersRead));
        Assert.False(RolePermissions.Has(MembershipRole.Customer, TenantPermissions.MembersManage));
        Assert.False(RolePermissions.Has(MembershipRole.Customer, TenantPermissions.OrdersManage));
        Assert.False(RolePermissions.Has(MembershipRole.Customer, TenantPermissions.SubscriptionsManage));
        Assert.True(RolePermissions.Has(MembershipRole.Customer, TenantPermissions.TenantRead));
    }

    [Fact]
    public void An_undefined_role_holds_nothing_because_it_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RolePermissions.For((MembershipRole)0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RolePermissions.Has((MembershipRole)99, TenantPermissions.TenantRead));
    }

    [Theory]
    [InlineData(MembershipRole.TenantAdmin, "tenant-admin")]
    [InlineData(MembershipRole.Customer, "customer")]
    public void Role_names_round_trip(MembershipRole role, string name)
    {
        Assert.Equal(name, role.ToName());
        Assert.Equal(role, MembershipRoleNames.Parse(name));
        Assert.True(MembershipRoleNames.TryParse(name, out var parsed));
        Assert.Equal(role, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Customer")]
    [InlineData("TENANT-ADMIN")]
    [InlineData("admin")]
    [InlineData(" customer")]
    public void An_unknown_role_name_is_not_a_role_and_a_stored_one_fails_loudly(string? name)
    {
        Assert.False(MembershipRoleNames.TryParse(name, out _));
        Assert.Throws<InvalidOperationException>(() => MembershipRoleNames.Parse(name!));
    }

    [Fact]
    public void An_undefined_role_has_no_name()
    {
        Assert.Throws<InvalidOperationException>(() => ((MembershipRole)0).ToName());
    }

    [Theory]
    [InlineData(MembershipStatus.Active, "active")]
    [InlineData(MembershipStatus.Removed, "removed")]
    public void Status_names_round_trip_and_unknown_ones_fail_loudly(MembershipStatus status, string name)
    {
        Assert.Equal(name, status.ToName());
        Assert.Equal(status, MembershipStatusNames.Parse(name));
        Assert.Throws<InvalidOperationException>(() => MembershipStatusNames.Parse("Active"));
        Assert.Throws<InvalidOperationException>(() => ((MembershipStatus)0).ToName());
    }
}
