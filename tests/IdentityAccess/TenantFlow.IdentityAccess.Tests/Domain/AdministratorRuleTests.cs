using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Abstractions.Identity;
using TenantFlow.IdentityAccess.Domain.Memberships;

namespace TenantFlow.IdentityAccess.Tests.Domain;

/// <summary>SPEC-0002 criterion 20: a tenant that has had an administrator keeps at least one.</summary>
public sealed class AdministratorRuleTests
{
    private static Membership Active(MembershipRole role) =>
        Membership.Create(
            new MembershipId(Guid.CreateVersion7()),
            new TenantId(Guid.CreateVersion7()),
            new ActorIdentity("https://idp.test/realms/tenantflow", "user-1"),
            role,
            DateTimeOffset.UnixEpoch);

    [Fact]
    public void The_only_administrator_cannot_be_demoted_or_removed()
    {
        var admin = Active(MembershipRole.TenantAdmin);

        var demote = Assert.Throws<AppException>(() => AdministratorRule.EnsureTenantKeepsAdministrator(admin, MembershipRole.Customer, activeAdministrators: 1));
        var remove = Assert.Throws<AppException>(() => AdministratorRule.EnsureTenantKeepsAdministrator(admin, roleAfter: null, activeAdministrators: 1));

        Assert.Equal("membership.last_administrator", demote.Error.Code);
        Assert.Equal("membership.last_administrator", remove.Error.Code);
        Assert.Equal(ErrorKind.Conflict, demote.Error.Kind);
    }

    [Fact]
    public void With_another_administrator_present_an_administrator_can_go()
    {
        var admin = Active(MembershipRole.TenantAdmin);

        AdministratorRule.EnsureTenantKeepsAdministrator(admin, MembershipRole.Customer, activeAdministrators: 2);
        AdministratorRule.EnsureTenantKeepsAdministrator(admin, roleAfter: null, activeAdministrators: 2);
    }

    [Fact]
    public void Staying_an_administrator_is_always_allowed()
    {
        var admin = Active(MembershipRole.TenantAdmin);

        AdministratorRule.EnsureTenantKeepsAdministrator(admin, MembershipRole.TenantAdmin, activeAdministrators: 1);
    }

    [Fact]
    public void A_customer_can_be_changed_or_removed_whatever_the_administrator_count()
    {
        var customer = Active(MembershipRole.Customer);

        AdministratorRule.EnsureTenantKeepsAdministrator(customer, MembershipRole.TenantAdmin, activeAdministrators: 0);
        AdministratorRule.EnsureTenantKeepsAdministrator(customer, roleAfter: null, activeAdministrators: 1);
    }
}
