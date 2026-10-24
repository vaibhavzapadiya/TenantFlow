namespace TenantFlow.IdentityAccess.Domain.Auditing;

/// <summary>The audit action names for membership changes: a stable contract, written to the database as they are.</summary>
public static class MembershipAuditActions
{
    public const string Added = "membership.added";
    public const string RoleChanged = "membership.role_changed";
    public const string Removed = "membership.removed";
}
