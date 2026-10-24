namespace TenantFlow.Api.Endpoints.V1.Tenants.Members.Schemas;

/// <summary>Body of <c>POST /tenants/{tenantId}/members</c>. Rules: <c>AddMemberCommandValidator</c> (ADR-0014).</summary>
internal sealed record AddMemberRequest(string? Subject, string? Role);
