using TenantFlow.BuildingBlocks.Abstractions.Identity;
using TenantFlow.IdentityAccess.Domain.Memberships;

namespace TenantFlow.IdentityAccess.Domain.Auditing;

/// <summary>
/// Append-only record of a membership change (blueprint section 13; SPEC-0002 criterion 21). Never updated or deleted.
/// A record can only be made through a factory, from the membership the change was made to, so an action can never
/// contradict the roles it describes.
/// </summary>
public sealed class MembershipAuditRecord
{
    // Also the constructor the database mapping uses.
    private MembershipAuditRecord(
        TenantId tenantId,
        MembershipId membershipId,
        string action,
        string actorIssuer,
        string actorSubject,
        MembershipRole? oldRole,
        MembershipRole? newRole,
        DateTimeOffset occurredAt)
    {
        TenantId = tenantId;
        MembershipId = membershipId;
        Action = action;
        ActorIssuer = actorIssuer;
        ActorSubject = actorSubject;
        OldRole = oldRole;
        NewRole = newRole;
        OccurredAt = occurredAt;
    }

    public long Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public MembershipId MembershipId { get; private set; }

    public string Action { get; private set; }

    public string ActorIssuer { get; private set; }

    public string ActorSubject { get; private set; }

    public MembershipRole? OldRole { get; private set; }

    public MembershipRole? NewRole { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>A member was added with the membership's role. There is no previous role.</summary>
    public static MembershipAuditRecord Added(Membership membership, ActorIdentity actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(actor);
        return new(membership.TenantId, membership.Id, MembershipAuditActions.Added, actor.Issuer, actor.Subject, oldRole: null, membership.Role, at);
    }

    /// <summary>A member's role went from <paramref name="oldRole"/> to the membership's current role.</summary>
    /// <exception cref="ArgumentException">The role did not change: that is not an audit event.</exception>
    public static MembershipAuditRecord RoleChanged(Membership membership, MembershipRole oldRole, ActorIdentity actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(actor);
        if (oldRole == membership.Role)
        {
            throw new ArgumentException("A role change needs a different role.", nameof(oldRole));
        }

        return new(membership.TenantId, membership.Id, MembershipAuditActions.RoleChanged, actor.Issuer, actor.Subject, oldRole, membership.Role, at);
    }

    /// <summary>A member was removed. The record keeps the role they held and has no new role.</summary>
    public static MembershipAuditRecord Removed(Membership membership, ActorIdentity actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(actor);
        return new(membership.TenantId, membership.Id, MembershipAuditActions.Removed, actor.Issuer, actor.Subject, membership.Role, newRole: null, at);
    }
}
