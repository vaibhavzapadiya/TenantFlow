using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Abstractions.Identity;
using TenantFlow.IdentityAccess.Application.TenantAccess;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.IdentityAccess.Domain.Memberships;
using TenantFlow.Tenants.Contracts;

namespace TenantFlow.IdentityAccess.Tests.TenantAccess;

/// <summary>
/// SPEC-0002 criteria 12 to 15 at the unit level: who may resolve a tenant context, in which order the checks run (so a
/// denial reveals nothing extra), and that the context fails closed. Query behaviour is proven in the database tests.
/// </summary>
public sealed class TenantAccessResolverTests
{
    private const string Issuer = "https://idp.test/realms/tenantflow";
    private static readonly TenantId TenantA = new(Guid.CreateVersion7());
    private static readonly TenantId TenantB = new(Guid.CreateVersion7());
    private static readonly ActorIdentity Admin = new(Issuer, "admin-a");
    private static readonly ActorIdentity Customer = new(Issuer, "customer-a");
    private static readonly ActorIdentity Stranger = new(Issuer, "stranger");

    private readonly FakeMembershipStore _store = new();
    private readonly FakeTenantDirectory _tenants = new();
    private readonly TenantAccessResolver _resolver;

    public TenantAccessResolverTests()
    {
        _resolver = new TenantAccessResolver(_store, _tenants);
        _tenants.Set(TenantA, TenantAvailability.Active);
        _tenants.Set(TenantB, TenantAvailability.Active);
        _store.Seed(TenantA, Admin, MembershipRole.TenantAdmin);
        _store.Seed(TenantA, Customer, MembershipRole.Customer);
    }

    [Fact]
    public async Task A_member_with_the_permission_resolves_and_the_context_reports_the_tenant_and_role()
    {
        await _resolver.ResolveAsync(TenantA.Value, Admin, TenantPermissions.MembersManage, Ct);

        Assert.True(_resolver.IsSet);
        Assert.Equal(TenantA.Value, _resolver.TenantId);
        Assert.Equal("tenant-admin", _resolver.Role);
        Assert.NotEqual(Guid.Empty, _resolver.MembershipId);
        Assert.True(_resolver.HasPermission(TenantPermissions.MembersManage));
        Assert.True(_resolver.HasPermission(TenantPermissions.TenantRead));
    }

    [Fact]
    public async Task A_customer_resolves_for_read_and_the_context_does_not_claim_more()
    {
        await _resolver.ResolveAsync(TenantA.Value, Customer, TenantPermissions.TenantRead, Ct);

        Assert.Equal("customer", _resolver.Role);
        Assert.True(_resolver.HasPermission(TenantPermissions.TenantRead));
        Assert.False(_resolver.HasPermission(TenantPermissions.MembersManage));
    }

    [Fact]
    public async Task A_non_member_and_an_unknown_tenant_get_the_same_not_found_and_no_context()
    {
        var nonMember = await Assert.ThrowsAsync<AppException>(() => _resolver.ResolveAsync(TenantB.Value, Admin, TenantPermissions.TenantRead, Ct));
        var unknown = await Assert.ThrowsAsync<AppException>(() => new TenantAccessResolver(_store, _tenants).ResolveAsync(Guid.CreateVersion7(), Admin, TenantPermissions.TenantRead, Ct));
        var empty = await Assert.ThrowsAsync<AppException>(() => new TenantAccessResolver(_store, _tenants).ResolveAsync(Guid.Empty, Admin, TenantPermissions.TenantRead, Ct));

        Assert.Equal("tenant.not_found", nonMember.Error.Code);
        Assert.Equal(nonMember.Error, unknown.Error);
        Assert.Equal(nonMember.Error, empty.Error);
        Assert.False(_resolver.IsSet);
    }

    [Fact]
    public async Task A_removed_membership_does_not_resolve()
    {
        _store.Seed(TenantB, Stranger, MembershipRole.Customer).Remove(DateTimeOffset.UnixEpoch);

        var error = await Assert.ThrowsAsync<AppException>(() => _resolver.ResolveAsync(TenantB.Value, Stranger, TenantPermissions.TenantRead, Ct));

        Assert.Equal("tenant.not_found", error.Error.Code);
    }

    [Fact]
    public async Task The_same_subject_from_another_issuer_does_not_resolve()
    {
        var error = await Assert.ThrowsAsync<AppException>(
            () => _resolver.ResolveAsync(TenantA.Value, new ActorIdentity("https://other.test", Admin.Subject), TenantPermissions.TenantRead, Ct));

        Assert.Equal("tenant.not_found", error.Error.Code);
    }

    [Fact]
    public async Task A_member_of_a_suspended_tenant_is_forbidden_but_a_non_member_still_sees_not_found()
    {
        _tenants.Set(TenantA, TenantAvailability.Suspended);

        var member = await Assert.ThrowsAsync<AppException>(() => _resolver.ResolveAsync(TenantA.Value, Admin, TenantPermissions.TenantRead, Ct));
        var stranger = await Assert.ThrowsAsync<AppException>(() => new TenantAccessResolver(_store, _tenants).ResolveAsync(TenantA.Value, Stranger, TenantPermissions.TenantRead, Ct));

        Assert.Equal("membership.tenant_suspended", member.Error.Code);
        Assert.Equal(ErrorKind.Forbidden, member.Error.Kind);
        Assert.Equal("tenant.not_found", stranger.Error.Code);
        Assert.False(_resolver.IsSet);
    }

    [Fact]
    public async Task A_membership_of_a_tenant_that_no_longer_exists_does_not_resolve()
    {
        var orphan = new TenantId(Guid.CreateVersion7());
        _store.Seed(orphan, Stranger, MembershipRole.TenantAdmin);

        var error = await Assert.ThrowsAsync<AppException>(() => _resolver.ResolveAsync(orphan.Value, Stranger, TenantPermissions.TenantRead, Ct));

        Assert.Equal("tenant.not_found", error.Error.Code);
    }

    [Theory]
    [InlineData(TenantPermissions.MembersRead)]
    [InlineData(TenantPermissions.MembersManage)]
    public async Task A_member_whose_role_lacks_the_permission_is_forbidden_and_no_context_is_set(string permission)
    {
        var error = await Assert.ThrowsAsync<AppException>(() => _resolver.ResolveAsync(TenantA.Value, Customer, permission, Ct));

        Assert.Equal("membership.permission_denied", error.Error.Code);
        Assert.Equal(ErrorKind.Forbidden, error.Error.Kind);
        Assert.False(_resolver.IsSet);
    }

    [Fact]
    public async Task The_context_can_be_set_once_per_request()
    {
        await _resolver.ResolveAsync(TenantA.Value, Admin, TenantPermissions.TenantRead, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _resolver.ResolveAsync(TenantA.Value, Customer, TenantPermissions.TenantRead, Ct));

        Assert.Equal("tenant-admin", _resolver.Role);
    }

    [Fact]
    public void With_nothing_resolved_every_member_fails_closed()
    {
        Assert.False(_resolver.IsSet);
        Assert.Throws<TenantContextNotSetException>(() => _resolver.TenantId);
        Assert.Throws<TenantContextNotSetException>(() => _resolver.MembershipId);
        Assert.Throws<TenantContextNotSetException>(() => _resolver.Role);
        Assert.Throws<TenantContextNotSetException>(() => _resolver.HasPermission(TenantPermissions.TenantRead));
    }

    [Fact]
    public async Task A_missing_caller_or_permission_is_a_programming_error()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _resolver.ResolveAsync(TenantA.Value, null!, TenantPermissions.TenantRead, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _resolver.ResolveAsync(TenantA.Value, Admin, " ", Ct));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
