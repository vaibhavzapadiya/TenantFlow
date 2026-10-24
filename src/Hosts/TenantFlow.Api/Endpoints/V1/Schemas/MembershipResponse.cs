using TenantFlow.IdentityAccess.Application.Memberships.Dtos;

namespace TenantFlow.Api.Endpoints.V1.Schemas;

/// <summary>A membership as the API returns it. The role is its stable name.</summary>
internal sealed record MembershipResponse(Guid Id, Guid TenantId, string Issuer, string Subject, string Role)
{
    public static MembershipResponse From(MembershipDetails membership) =>
        new(membership.Id, membership.TenantId, membership.Issuer, membership.Subject, membership.Role);
}
