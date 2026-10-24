namespace TenantFlow.IdentityAccess.Domain.Memberships;

/// <summary>
/// What a member may do inside one tenant (SPEC-0002 Contract). Values start at 1, so an unset
/// <c>default</c> is never a real role.
/// </summary>
public enum MembershipRole
{
    TenantAdmin = 1,
    Customer = 2,
}
