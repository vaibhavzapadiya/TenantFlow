using TenantFlow.Api.Endpoints.V1.Me.Schemas;
using TenantFlow.Api.Extensions;
using TenantFlow.BuildingBlocks.Abstractions.Identity;
using TenantFlow.IdentityAccess.Application.Memberships.Services;

namespace TenantFlow.Api.Endpoints.V1.Me;

/// <summary>
/// <c>GET /me</c>: who the caller is, as the token says (SPEC-0002 criterion 5). Identity is issuer plus subject
/// (ADR-0009); it grants no tenant authority on its own. <c>GET /me/memberships</c>: the caller's own active
/// memberships in active tenants (criterion 16). Both only ever describe the caller, so neither takes a tenant id.
/// </summary>
internal static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var me = endpoints.MapGroup("/me")
            .WithTags("Me")
            .RequireAuthorization(AuthenticationExtensions.TenantAuthenticatedUserPolicy);

        me.MapGet("/", static (HttpContext context) =>
            TypedResults.Ok(new MeResponse(
                context.User.FindFirst("iss")!.Value,
                context.User.FindFirst("sub")!.Value)));

        me.MapGet("/memberships", static async (HttpContext context, MembershipService service, CancellationToken cancellationToken) =>
        {
            // The tenant scheme guarantees both claims (issuer validation and the required subject).
            var caller = new ActorIdentity(context.User.FindFirst("iss")!.Value, context.User.FindFirst("sub")!.Value);
            return TypedResults.Ok(MyMembershipsResponse.From(await service.ListForIdentityAsync(caller, cancellationToken)));
        });
        return endpoints;
    }
}
