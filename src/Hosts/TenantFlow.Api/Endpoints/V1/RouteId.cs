using TenantFlow.BuildingBlocks.Abstractions.Errors;

namespace TenantFlow.Api.Endpoints.V1;

/// <summary>
/// The one rule for ids in routes: only the canonical 36-character form names a resource, and anything else answers
/// exactly like an unknown one (404 with the resource's own not-found code), never 400. A probe therefore cannot tell
/// "malformed" from "absent". <see cref="TenantRouteId"/>, <see cref="MembershipRouteId"/> and the product route id use it.
/// </summary>
internal static class RouteId
{
    /// <exception cref="AppException"><paramref name="notFound"/> when the value is not a canonical id.</exception>
    public static Guid Parse(string value, AppError notFound) =>
        Guid.TryParseExact(value, "D", out var id) ? id : throw new AppException(notFound);
}
