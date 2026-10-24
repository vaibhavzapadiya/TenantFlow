using TenantFlow.IdentityAccess.Domain.Memberships;

namespace TenantFlow.IdentityAccess.Application.Memberships.Dtos;

/// <summary>One member of the current tenant as an administrator sees them. Role by its stable name.</summary>
public sealed record MemberDetails(Guid Id, string Subject, string Role)
{
    internal static MemberDetails From(Membership membership) => new(membership.Id.Value, membership.Subject, membership.Role.ToName());
}
