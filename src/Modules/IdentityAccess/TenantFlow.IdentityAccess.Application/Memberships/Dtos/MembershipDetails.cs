using TenantFlow.IdentityAccess.Domain.Memberships;

namespace TenantFlow.IdentityAccess.Application.Memberships.Dtos;

/// <summary>A membership as a use case returns it: plain values, with the role by its stable name.</summary>
public sealed record MembershipDetails(Guid Id, Guid TenantId, string Issuer, string Subject, string Role)
{
    internal static MembershipDetails From(Membership membership) =>
        new(membership.Id.Value, membership.TenantId.Value, membership.Issuer, membership.Subject, membership.Role.ToName());
}
