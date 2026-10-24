using TenantFlow.Audit.Contracts;
using TenantFlow.BuildingBlocks.Abstractions.Identity;
using TenantFlow.IdentityAccess.Application.Abstractions;
using TenantFlow.IdentityAccess.Application.Memberships.Commands;
using TenantFlow.IdentityAccess.Application.Memberships.Dtos;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.IdentityAccess.Domain.Memberships;

namespace TenantFlow.IdentityAccess.Application.Memberships.Services;

/// <summary>
/// The tenant-owned member use cases (SPEC-0002 criteria 17 to 21): list, add, change role, remove. Every one acts on
/// the tenant in the <see cref="ITenantContext"/> and takes no tenant id of its own, so it cannot be pointed at another
/// tenant, and with no context set it throws before touching data (criterion 15). The route has already checked the
/// caller's permission; membership ids are looked up inside the context's tenant, so an id from another tenant is
/// simply not found. The central audit log names the caller by the acting membership of the context, never by the
/// token's subject (SPEC-0009 criterion 2).
/// </summary>
public sealed class TenantMembersService(ITenantContext context, IMembershipStore store, MembershipService memberships)
{
    public async Task<IReadOnlyList<MemberDetails>> ListAsync(CancellationToken cancellationToken)
    {
        var tenantId = new TenantId(context.TenantId);
        var members = await store.ListActiveInTenantAsync(tenantId, cancellationToken);
        return [.. members.Select(MemberDetails.From)];
    }

    /// <summary>Adds a member who signs in through the same identity provider as the caller.</summary>
    public Task<MembershipDetails> AddAsync(string? subject, string? role, ActorIdentity actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return memberships.AddAsync(new AddMemberCommand(context.TenantId, actor.Issuer, subject, role), actor, Caller(), cancellationToken);
    }

    public Task<MembershipDetails> ChangeRoleAsync(Guid membershipId, string? role, ActorIdentity actor, CancellationToken cancellationToken) =>
        memberships.ChangeRoleAsync(new ChangeRoleCommand(context.TenantId, membershipId, role), actor, Caller(), cancellationToken);

    public Task RemoveAsync(Guid membershipId, ActorIdentity actor, CancellationToken cancellationToken) =>
        memberships.RemoveAsync(context.TenantId, membershipId, actor, Caller(), cancellationToken);

    // Throws, like every other member of the context, when no tenant was resolved.
    private AuditActor Caller() => AuditActor.Member(context.MembershipId);
}
