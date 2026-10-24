using TenantFlow.IdentityAccess.Application.Memberships.Dtos;

namespace TenantFlow.Api.Endpoints.V1.Me.Schemas;

/// <summary>
/// The body of <c>GET /v1/me/memberships</c>: the caller's own active memberships in active tenants. A wrapper object
/// rather than a bare array, so a field (paging, say) can be added later without breaking clients.
/// </summary>
internal sealed record MyMembershipsResponse(IReadOnlyList<MyMembershipsResponse.Membership> Items)
{
    internal static MyMembershipsResponse From(IReadOnlyList<MembershipSummary> memberships) =>
        new([.. memberships.Select(static m => new Membership(m.MembershipId, m.TenantId, m.Role))]);

    internal sealed record Membership(Guid MembershipId, Guid TenantId, string Role);
}
