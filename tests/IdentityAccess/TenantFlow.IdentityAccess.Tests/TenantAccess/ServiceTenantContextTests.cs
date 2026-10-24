using TenantFlow.IdentityAccess.Application.TenantAccess;
using TenantFlow.IdentityAccess.Contracts;

namespace TenantFlow.IdentityAccess.Tests.TenantAccess;

/// <summary>ADR-0022 decisions 6 and 7: the Worker's tenant context acts for one tenant, for no membership, with no permission.</summary>
public sealed class ServiceTenantContextTests
{
    [Fact]
    public void Unentered_it_fails_closed_and_entered_it_grants_no_permission_or_membership()
    {
        var context = new ServiceTenantContext();
        var tenant = Guid.CreateVersion7();

        Assert.False(context.IsSet);
        Assert.Throws<TenantContextNotSetException>(() => context.TenantId);
        context.Enter(tenant);

        Assert.Equal((true, tenant, ServiceTenantContext.ServiceRole), (context.IsSet, context.TenantId, context.Role));
        Assert.False(context.HasPermission(TenantPermissions.OrdersManage));
        Assert.Throws<TenantContextNotSetException>(() => context.MembershipId);
        Assert.Throws<InvalidOperationException>(() => context.Enter(Guid.CreateVersion7()));
        Assert.Equal(tenant, context.TenantId);
    }
}
