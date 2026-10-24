using System.Security.Claims;
using TenantFlow.Api.Endpoints.V1.Schemas;
using TenantFlow.Api.Endpoints.V1.TenantAccess;
using TenantFlow.Api.Endpoints.V1.Tenants.Members.Schemas;
using TenantFlow.BuildingBlocks.Abstractions.Identity;
using TenantFlow.IdentityAccess.Application.Memberships.Services;
using TenantFlow.IdentityAccess.Contracts;

namespace TenantFlow.Api.Endpoints.V1.Tenants.Members;

/// <summary>
/// The tenant's member routes (SPEC-0002 criteria 17 to 21; ADR-0010). Every one declares its permission through
/// <c>RequireTenantPermission</c>, and handlers act on the tenant context, never on the route's text. Failures are thrown
/// as <c>AppException</c> or validation errors (ADR-0016): 404 <c>membership.not_found</c>, 409
/// <c>membership.already_exists</c> or <c>membership.last_administrator</c>, 400 for invalid input.
/// </summary>
internal static class MemberEndpoints
{
    public static IEndpointRouteBuilder MapMemberEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var members = endpoints.MapGroup("/tenants/{tenantId}/members")
            .WithTags("Members");

        members.MapGet("/", static async (TenantMembersService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(MembersResponse.From(await service.ListAsync(cancellationToken))))
            .RequireTenantPermission(TenantPermissions.MembersRead);

        members.MapPost("/", static async (AddMemberRequest request, ClaimsPrincipal user, TenantMembersService service, CancellationToken cancellationToken) =>
            {
                var added = await service.AddAsync(request.Subject, request.Role, ActorOf(user), cancellationToken);
                // No Location: a single member has no route of its own.
                return TypedResults.Json(MembershipResponse.From(added), statusCode: StatusCodes.Status201Created);
            })
            .RequireTenantPermission(TenantPermissions.MembersManage);

        members.MapPut("/{membershipId}/role", static async (string membershipId, ChangeMemberRoleRequest request, ClaimsPrincipal user, TenantMembersService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(MembershipResponse.From(
                    await service.ChangeRoleAsync(MembershipRouteId.Parse(membershipId), request.Role, ActorOf(user), cancellationToken))))
            .RequireTenantPermission(TenantPermissions.MembersManage);

        members.MapDelete("/{membershipId}", static async (string membershipId, ClaimsPrincipal user, TenantMembersService service, CancellationToken cancellationToken) =>
            {
                await service.RemoveAsync(MembershipRouteId.Parse(membershipId), ActorOf(user), cancellationToken);
                return TypedResults.NoContent();
            })
            .RequireTenantPermission(TenantPermissions.MembersManage);

        return endpoints;
    }

    // The tenant scheme guarantees both claims (issuer validation and the required subject).
    private static ActorIdentity ActorOf(ClaimsPrincipal user) =>
        new(user.FindFirst("iss")!.Value, user.FindFirst("sub")!.Value);
}
