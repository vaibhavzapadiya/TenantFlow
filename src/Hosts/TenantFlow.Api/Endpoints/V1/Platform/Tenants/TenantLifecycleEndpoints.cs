using System.Security.Claims;
using TenantFlow.Api.Auditing;
using TenantFlow.Api.Endpoints.V1.Platform.Tenants.Schemas;
using TenantFlow.Api.Extensions;
using TenantFlow.BuildingBlocks.Abstractions.Identity;
using TenantFlow.Tenants.Application.Lifecycle.Commands;
using TenantFlow.Tenants.Application.Lifecycle.Services;

namespace TenantFlow.Api.Endpoints.V1.Platform.Tenants;

/// <summary>
/// Tenant lifecycle on the platform boundary (SPEC-0002 criteria 24 and 25; ADR-0010). Only platform
/// operators reach these routes; tenant tokens fail authentication here. Failures are thrown as
/// <c>AppException</c> or validation errors and answered by <c>ExpectedFailureMiddleware</c> (ADR-0016):
/// 400 invalid input, 404 <c>tenant.not_found</c>, 409 <c>tenant.already_suspended</c>,
/// <c>tenant.not_suspended</c> or <c>tenant.concurrent_update</c>.
/// </summary>
internal static class TenantLifecycleEndpoints
{
    // The Location header of a created tenant is generated from this route name, so it always carries the version.
    private const string GetTenantRoute = "GetPlatformTenant";

    public static IEndpointRouteBuilder MapTenantLifecycleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var tenants = endpoints.MapGroup("/platform/tenants")
            .WithTags("Platform: Tenants")
            .RequireAuthorization(AuthenticationExtensions.PlatformAdminPolicy);

        tenants.MapPost("/", static async (CreateTenantRequest request, HttpContext context, LinkGenerator links, OperatorReferences operators, TenantLifecycleService service, CancellationToken cancellationToken) =>
        {
            var tenant = await service.CreateAsync(new CreateTenantCommand(request.Name), ActorOf(context.User), operators.PlatformAdmin(context.User), cancellationToken);
            // A relative Location, generated from the named route so it always carries the version. Not
            // CreatedAtRoute: that builds an absolute URL from the request's Host header (blueprint section 9).
            var location = links.GetPathByName(context, GetTenantRoute, new { id = tenant.Id });
            return TypedResults.Created(location, TenantResponse.From(tenant));
        });

        tenants.MapGet("/{id}", static async (string id, TenantLifecycleService service, CancellationToken cancellationToken) =>
            TypedResults.Ok(TenantResponse.From(await service.GetAsync(TenantRouteId.Parse(id), cancellationToken))))
            .WithName(GetTenantRoute);

        tenants.MapPost("/{id}/suspend", static async (string id, ClaimsPrincipal user, OperatorReferences operators, TenantLifecycleService service, CancellationToken cancellationToken) =>
            TypedResults.Ok(TenantResponse.From(await service.SuspendAsync(TenantRouteId.Parse(id), ActorOf(user), operators.PlatformAdmin(user), cancellationToken))));

        tenants.MapPost("/{id}/reactivate", static async (string id, ClaimsPrincipal user, OperatorReferences operators, TenantLifecycleService service, CancellationToken cancellationToken) =>
            TypedResults.Ok(TenantResponse.From(await service.ReactivateAsync(TenantRouteId.Parse(id), ActorOf(user), operators.PlatformAdmin(user), cancellationToken))));

        return endpoints;
    }

    // The platform scheme guarantees both claims (issuer validation and the required subject).
    internal static ActorIdentity ActorOf(ClaimsPrincipal user) =>
        new ActorIdentity(user.FindFirst("iss")!.Value, user.FindFirst("sub")!.Value);
}
