using System.Security.Claims;
using Microsoft.Extensions.Options;
using TenantFlow.Api.Auditing;
using TenantFlow.Api.Configuration;
using TenantFlow.Api.Endpoints.V1.Platform.Tenants.Schemas;
using TenantFlow.Api.Endpoints.V1.Schemas;
using TenantFlow.Api.Extensions;
using TenantFlow.IdentityAccess.Application.Memberships.Services;

namespace TenantFlow.Api.Endpoints.V1.Platform.Tenants;

/// <summary>
/// <c>POST /platform/tenants/{id}/admins</c>: a platform operator makes a user the administrator of a tenant
/// (SPEC-0002 criterion 24; ADR-0010). It is a separate call from tenant creation, so no transaction spans two
/// modules. Failures are answered by <c>ExpectedFailureMiddleware</c> (ADR-0016): 400 invalid subject, 404
/// <c>tenant.not_found</c>, 409 <c>tenant.suspended</c> or <c>membership.already_exists</c>.
/// </summary>
internal static class TenantAdministratorEndpoints
{
    public static IEndpointRouteBuilder MapTenantAdministratorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var tenants = endpoints.MapGroup("/platform/tenants")
            .WithTags("Platform: Tenants")
            .RequireAuthorization(AuthenticationExtensions.PlatformAdminPolicy);

        tenants.MapPost("/{id}/admins", static async (
            string id,
            AddTenantAdministratorRequest request,
            ClaimsPrincipal user,
            IOptionsMonitor<OidcIssuerOptions> issuers,
            OperatorReferences operators,
            MembershipService service,
            CancellationToken cancellationToken) =>
        {
            var tenantIssuer = issuers.Get(AuthenticationExtensions.TenantScheme).Authority;
            var administrator = await service.AddTenantAdministratorAsync(
                TenantRouteId.Parse(id), tenantIssuer, request.Subject, TenantLifecycleEndpoints.ActorOf(user), operators.PlatformAdmin(user), cancellationToken);
            // No Location yet: the member routes arrive in S6.
            return TypedResults.Json(MembershipResponse.From(administrator), statusCode: StatusCodes.Status201Created);
        });
        return endpoints;
    }
}
