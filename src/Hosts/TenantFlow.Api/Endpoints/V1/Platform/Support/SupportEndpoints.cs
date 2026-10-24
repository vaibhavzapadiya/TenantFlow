using System.Security.Claims;
using TenantFlow.Api.Auditing;
using TenantFlow.Api.Extensions;
using TenantFlow.Operations.Application.SupportGrants;
using TenantFlow.Operations.Domain.SupportGrants;

namespace TenantFlow.Api.Endpoints.V1.Platform.Support;

/// <summary>
/// Support grants and the read-only support views on the platform boundary (SPEC-0009 criteria 15 to 18, ADR-0027).
/// Support operators open and end their own grants and read through them; platform administrators may revoke any grant
/// and nothing else here. Every view commits its access record before it reads. Failures (ADR-0016): 400
/// <c>support.tenant_required</c>, <c>support.ticket_invalid</c> or <c>support.duration_invalid</c>; 404
/// <c>tenant.not_found</c> (also without an active grant) or <c>support.grant_not_found</c>; 409 <c>support.grant_exists</c>.
/// </summary>
internal static class SupportEndpoints
{
    public static IEndpointRouteBuilder MapSupportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var support = endpoints.MapGroup("/platform/support")
            .WithTags("Platform: Support");

        support.MapPost("/grants", static async (OpenSupportGrantRequest request, ClaimsPrincipal user, OperatorReferences operators, SupportService service, CancellationToken cancellationToken) =>
                TypedResults.Json(
                    SupportGrantResponse.From(await service.OpenAsync(request.TenantId, request.Ticket, request.Minutes, operators.SupportOperator(user), cancellationToken)),
                    statusCode: StatusCodes.Status201Created))
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireAuthorization(AuthenticationExtensions.SupportOperatorPolicy);

        support.MapDelete("/grants/{grantId}", static async (string grantId, ClaimsPrincipal user, OperatorReferences operators, SupportService service, CancellationToken cancellationToken) =>
            {
                // A platform administrator revokes as such; otherwise only the grant's own operator may end it.
                var caller = user.HasClaim(AuthenticationExtensions.RolesClaim, AuthenticationExtensions.PlatformAdminRole)
                    ? operators.PlatformAdmin(user)
                    : operators.SupportOperator(user);
                await service.EndAsync(RouteId.Parse(grantId, OperationsErrors.GrantNotFound), caller, cancellationToken);
                return TypedResults.NoContent();
            })
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthenticationExtensions.PlatformStaffPolicy);

        support.MapGet("/tenants/{tenantId}/members", static async (string tenantId, ClaimsPrincipal user, OperatorReferences operators, SupportService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(new SupportListResponse<SupportMemberResponse>(
                    [.. (await service.MembersAsync(TenantRouteId.Parse(tenantId), operators.SupportOperator(user), cancellationToken)).Select(SupportMemberResponse.From)])))
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthenticationExtensions.SupportOperatorPolicy);

        support.MapGet("/tenants/{tenantId}/jobs", static async (string tenantId, ClaimsPrincipal user, OperatorReferences operators, SupportService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(new SupportListResponse<SupportJobResponse>(
                    [.. (await service.JobsAsync(TenantRouteId.Parse(tenantId), operators.SupportOperator(user), cancellationToken)).Select(SupportJobResponse.From)])))
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization(AuthenticationExtensions.SupportOperatorPolicy);

        return endpoints;
    }
}
