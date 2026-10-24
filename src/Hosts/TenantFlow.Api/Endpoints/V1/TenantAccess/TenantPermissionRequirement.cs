namespace TenantFlow.Api.Endpoints.V1.TenantAccess;

/// <summary>
/// Endpoint metadata: the permission a tenant route requires. The access filter enforces it, and the route-table test
/// (SPEC-0002 criterion 22) reads it to prove no tenant route lacks one.
/// </summary>
internal sealed record TenantPermissionRequirement(string Permission);
