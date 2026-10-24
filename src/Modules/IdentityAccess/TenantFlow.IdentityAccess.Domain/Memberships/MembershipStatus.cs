namespace TenantFlow.IdentityAccess.Domain.Memberships;

/// <summary>A membership is active until it is removed. Removal is final: a returning member gets a new membership.</summary>
public enum MembershipStatus
{
    Active = 1,
    Removed = 2,
}
