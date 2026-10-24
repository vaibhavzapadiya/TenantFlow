namespace TenantFlow.Api.Endpoints.V1.Platform.Tenants.Schemas;

/// <summary>
/// Body of <c>POST /platform/tenants/{id}/admins</c>. Rules: <c>AddMemberCommandValidator</c> (ADR-0014). The issuer is
/// not part of the request: the new administrator signs in through the tenant identity provider, so the API uses the
/// issuer it trusts for tenant tokens (ADR-0009).
/// </summary>
internal sealed record AddTenantAdministratorRequest(string? Subject);
