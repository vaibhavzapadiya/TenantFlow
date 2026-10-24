using TenantFlow.Api.Endpoints.V1.Tenants.Audit.Schemas;
using TenantFlow.Api.Extensions;
using TenantFlow.Audit.Application.Records.Services;

namespace TenantFlow.Api.Endpoints.V1.Platform.Audit;

/// <summary>
/// One tenant's audit log on the platform boundary (SPEC-0009 criterion 5; ADR-0010). Only platform operators reach it;
/// tenant tokens fail authentication here. The tenant is named in the query and read under its own row-level security
/// scope. Failures (ADR-0016): 400 <c>audit.tenant_required</c> and the paging errors, 404 <c>tenant.not_found</c>.
/// </summary>
internal static class PlatformAuditEndpoints
{
    public static IEndpointRouteBuilder MapPlatformAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/platform/audit", static async (string? tenantId, int? limit, string? cursor, string? action, AuditLog log, CancellationToken cancellationToken) =>
                TypedResults.Ok(AuditPageResponse.From(await log.ListForPlatformAsync(tenantId, limit, cursor, action, cancellationToken))))
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthenticationExtensions.PlatformAdminPolicy)
            .WithTags("Platform: Audit");

        return endpoints;
    }
}
