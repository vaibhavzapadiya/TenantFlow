namespace TenantFlow.Api.Endpoints.V1.Platform.Tenants.Schemas;

/// <summary>Body of <c>POST /platform/tenants</c>. Rules: <c>CreateTenantCommandValidator</c> (ADR-0014).</summary>
internal sealed record CreateTenantRequest(string? Name);
