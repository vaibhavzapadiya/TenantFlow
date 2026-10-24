using TenantFlow.Api.Endpoints.V1.TenantAccess;
using TenantFlow.Api.Endpoints.V1.Tenants.Schemas;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.Tenants.Application.Lifecycle.Services;

namespace TenantFlow.Api.Endpoints.V1.Tenants;

/// <summary>
/// The tenant's own summary (SPEC-0002 criteria 12 to 14; ADR-0010). Like every tenant route it declares its permission
/// through <c>RequireTenantPermission</c>; the filter resolves the membership first, so the handler acts on the tenant
/// context and never on the route's text. A non-member or unknown tenant is 404 <c>tenant.not_found</c>.
/// </summary>
internal static class TenantEndpoints
{
    public static IEndpointRouteBuilder MapTenantEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/tenants/{tenantId}", static async (ITenantContext context, TenantLifecycleService tenants, CancellationToken cancellationToken) =>
                TypedResults.Ok(TenantSummaryResponse.From(await tenants.GetAsync(context.TenantId, cancellationToken))))
            .RequireTenantPermission(TenantPermissions.TenantRead)
            .WithTags("Tenants");

        return endpoints;
    }
}
