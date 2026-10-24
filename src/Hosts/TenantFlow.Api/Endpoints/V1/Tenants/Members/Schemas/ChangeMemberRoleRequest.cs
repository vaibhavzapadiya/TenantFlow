namespace TenantFlow.Api.Endpoints.V1.Tenants.Members.Schemas;

/// <summary>Body of <c>PUT /tenants/{tenantId}/members/{membershipId}/role</c>. Rules: <c>ChangeRoleCommandValidator</c>.</summary>
internal sealed record ChangeMemberRoleRequest(string? Role);
