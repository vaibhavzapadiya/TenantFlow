using TenantFlow.Audit.Contracts;
using TenantFlow.Audit.Contracts.Events;
using TenantFlow.IdentityAccess.Domain.Memberships;

namespace TenantFlow.IdentityAccess.Domain.Auditing;

/// <summary>
/// The central audit events of membership changes (SPEC-0009 criterion 2, ADR-0026), built here beside
/// <see cref="MembershipAuditRecord"/> from the membership the change was made to. The target is the membership id and
/// the actor a reference: the member's issuer and subject never leave this module.
/// </summary>
public static class MembershipAuditEvents
{
    public static AuditRecordedV1 Added(Membership membership, AuditActor actor, DateTimeOffset at) =>
        Of(AuditActions.MembershipAdded, membership, actor, at);

    public static AuditRecordedV1 RoleChanged(Membership membership, AuditActor actor, DateTimeOffset at) =>
        Of(AuditActions.MembershipRoleChanged, membership, actor, at);

    public static AuditRecordedV1 Removed(Membership membership, AuditActor actor, DateTimeOffset at) =>
        Of(AuditActions.MembershipRemoved, membership, actor, at);

    private static AuditRecordedV1 Of(string action, Membership membership, AuditActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(membership);
        return AuditRecordedV1.Of(action, actor, AuditTargetTypes.Membership, membership.Id.Value, at);
    }
}
