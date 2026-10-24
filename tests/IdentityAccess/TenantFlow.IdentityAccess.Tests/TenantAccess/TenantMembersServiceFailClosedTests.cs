using TenantFlow.BuildingBlocks.Abstractions.Identity;
using TenantFlow.IdentityAccess.Application.Memberships.Services;
using TenantFlow.IdentityAccess.Application.Memberships.Validators;
using TenantFlow.IdentityAccess.Application.TenantAccess;
using TenantFlow.IdentityAccess.Contracts;

namespace TenantFlow.IdentityAccess.Tests.TenantAccess;

/// <summary>
/// SPEC-0002 criterion 15: a tenant-owned use case that runs with no tenant context throws before it touches any data.
/// The store and directory count every call, so "before touching data" is checked, not assumed.
/// </summary>
public sealed class TenantMembersServiceFailClosedTests
{
    private static readonly ActorIdentity Actor = new("https://idp.test/realms/tenantflow", "admin-a");

    private readonly FakeMembershipStore _store = new();
    private readonly FakeTenantDirectory _tenants = new();
    private readonly TenantMembersService _service;

    public TenantMembersServiceFailClosedTests()
    {
        var unset = new TenantAccessResolver(_store, _tenants);
        var memberships = new MembershipService(
            _store,
            _tenants,
            new AddMemberCommandValidator(),
            new ChangeRoleCommandValidator(),
            TimeProvider.System);
        _service = new TenantMembersService(unset, _store, memberships);
    }

    [Fact]
    public async Task Listing_with_no_context_throws_and_reads_nothing()
    {
        await Assert.ThrowsAsync<TenantContextNotSetException>(() => _service.ListAsync(Ct));

        Assert.Equal(0, _store.Calls);
    }

    [Fact]
    public async Task Adding_with_no_context_throws_and_touches_no_data()
    {
        await Assert.ThrowsAsync<TenantContextNotSetException>(() => _service.AddAsync("user-1", "customer", Actor, Ct));

        Assert.Equal(0, _store.Calls);
        Assert.Equal(0, _tenants.Calls);
    }

    [Fact]
    public async Task Changing_a_role_with_no_context_throws_and_touches_no_data()
    {
        await Assert.ThrowsAsync<TenantContextNotSetException>(() => _service.ChangeRoleAsync(Guid.CreateVersion7(), "customer", Actor, Ct));

        Assert.Equal(0, _store.Calls);
    }

    [Fact]
    public async Task Removing_with_no_context_throws_and_touches_no_data()
    {
        await Assert.ThrowsAsync<TenantContextNotSetException>(() => _service.RemoveAsync(Guid.CreateVersion7(), Actor, Ct));

        Assert.Equal(0, _store.Calls);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
