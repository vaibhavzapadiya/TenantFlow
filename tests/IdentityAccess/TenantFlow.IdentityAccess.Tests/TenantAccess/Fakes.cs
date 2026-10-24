using TenantFlow.BuildingBlocks.Abstractions.Identity;
using TenantFlow.IdentityAccess.Application.Abstractions;
using TenantFlow.IdentityAccess.Domain.Memberships;
using TenantFlow.Tenants.Contracts;

namespace TenantFlow.IdentityAccess.Tests.TenantAccess;

/// <summary>
/// A membership store port with a few memberships in memory, counting every call. These tests are about orchestration
/// order and fail-closed behaviour; query behaviour is proven against real PostgreSQL in the database tests.
/// </summary>
internal sealed class FakeMembershipStore : IMembershipStore
{
    private readonly List<Membership> _memberships = [];

    public int Calls { get; private set; }

    public Membership Seed(TenantId tenant, ActorIdentity member, MembershipRole role)
    {
        var membership = Membership.Create(new MembershipId(Guid.CreateVersion7()), tenant, member, role, DateTimeOffset.UnixEpoch);
        _memberships.Add(membership);
        return membership;
    }

    public Task<Membership?> FindActiveAsync(TenantId tenantId, string issuer, string subject, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(_memberships.FirstOrDefault(m =>
            m.TenantId == tenantId && m.Issuer == issuer && m.Subject == subject && m.IsActive));
    }

    public Task<IReadOnlyList<Membership>> ListActiveInTenantAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<Membership>>([.. _memberships.Where(m => m.TenantId == tenantId && m.IsActive)]);
    }

    public Task<IReadOnlyList<Membership>> ListActiveForIdentityAsync(string issuer, string subject, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<Membership>>([.. _memberships.Where(m => m.Issuer == issuer && m.Subject == subject && m.IsActive)]);
    }

    public Task<IMembershipChange> BeginChangeAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        Calls++;
        throw new NotSupportedException("Changes are proven against real PostgreSQL.");
    }
}

internal sealed class FakeTenantDirectory : ITenantDirectory
{
    private readonly Dictionary<Guid, TenantAvailability> _tenants = [];

    public int Calls { get; private set; }

    public void Set(TenantId tenant, TenantAvailability availability) => _tenants[tenant.Value] = availability;

    public Task<IReadOnlyDictionary<Guid, TenantAvailability>> GetAvailabilityAsync(IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult<IReadOnlyDictionary<Guid, TenantAvailability>>(
            tenantIds.Where(_tenants.ContainsKey).ToDictionary(id => id, id => _tenants[id]));
    }

    public Task<IReadOnlyList<Guid>> ListIdsAsync(Guid? after, int limit, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Paging tenants is proven against real PostgreSQL.");
}
