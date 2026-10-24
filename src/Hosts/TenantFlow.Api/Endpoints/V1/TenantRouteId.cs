using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.Tenants.Domain.Tenants;

namespace TenantFlow.Api.Endpoints.V1;

/// <summary>
/// Reads a tenant id from a route. Only the canonical 36-character form can name a tenant; anything else
/// answers exactly like an unknown tenant (404 <c>tenant.not_found</c>), never 400 and never 401. A
/// <c>:guid</c> route constraint was the wrong tool: a non-matching route falls through to the
/// deny-by-default policy, so a signed-in operator who mistyped an id was told to authenticate.
/// </summary>
internal static class TenantRouteId
{
    /// <exception cref="AppException"><see cref="TenantErrors.NotFound"/> when the value is not a tenant id.</exception>
    public static Guid Parse(string value) =>
        RouteId.Parse(value, TenantErrors.NotFound);
}
