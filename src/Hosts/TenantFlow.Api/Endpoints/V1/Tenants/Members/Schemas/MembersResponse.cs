using TenantFlow.IdentityAccess.Application.Memberships.Dtos;

namespace TenantFlow.Api.Endpoints.V1.Tenants.Members.Schemas;

/// <summary>The body of <c>GET /tenants/{tenantId}/members</c>: a wrapper object, so paging can be added later.</summary>
internal sealed record MembersResponse(IReadOnlyList<MembersResponse.Member> Items)
{
    internal static MembersResponse From(IReadOnlyList<MemberDetails> members) =>
        new([.. members.Select(static m => new Member(m.Id, m.Subject, m.Role))]);

    internal sealed record Member(Guid Id, string Subject, string Role);
}
