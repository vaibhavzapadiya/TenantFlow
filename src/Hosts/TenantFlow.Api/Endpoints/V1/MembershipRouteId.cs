using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.IdentityAccess.Domain.Memberships;

namespace TenantFlow.Api.Endpoints.V1;

/// <summary>
/// Reads a membership id from a route. Like <see cref="TenantRouteId"/>, only the canonical form can name one;
/// anything else answers exactly like an unknown membership (404 <c>membership.not_found</c>).
/// </summary>
internal static class MembershipRouteId
{
    /// <exception cref="AppException"><see cref="MembershipErrors.NotFound"/> when the value is not a membership id.</exception>
    public static Guid Parse(string value) =>
        RouteId.Parse(value, MembershipErrors.NotFound);
}
