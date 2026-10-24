using TenantFlow.Api.Endpoints.V1.TenantAccess;
using TenantFlow.Api.Endpoints.V1.Tenants.Audit.Schemas;
using TenantFlow.Audit.Application.Records.Services;
using TenantFlow.IdentityAccess.Contracts;

namespace TenantFlow.Api.Endpoints.V1.Tenants.Audit;

/// <summary>
/// A tenant's audit log (SPEC-0009 criterion 4): <c>audit.read</c>, which only tenant administrators hold. The tenant is
/// the one the caller's membership was resolved in, never the route value on its own. Failures (ADR-0016): 400
/// <c>audit.limit_invalid</c>, <c>audit.cursor_invalid</c> or <c>audit.action_invalid</c>.
/// </summary>
internal static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/tenants/{tenantId}/audit", static async (int? limit, string? cursor, string? action, ITenantContext tenant, AuditLog log, CancellationToken cancellationToken) =>
                TypedResults.Ok(AuditPageResponse.From(await log.ListAsync(tenant.TenantId, limit, cursor, action, cancellationToken))))
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .RequireTenantPermission(TenantPermissions.AuditRead)
            .WithTags("Audit");

        return endpoints;
    }
}
