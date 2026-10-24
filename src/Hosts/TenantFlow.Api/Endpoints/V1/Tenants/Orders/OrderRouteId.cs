using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.Orders.Domain.Orders;

namespace TenantFlow.Api.Endpoints.V1.Tenants.Orders;

/// <summary>Reads an order id from a route: only the canonical form can name one, anything else is an unknown order.</summary>
internal static class OrderRouteId
{
    /// <exception cref="AppException"><see cref="OrderErrors.NotFound"/> when the value is not an order id.</exception>
    public static Guid Parse(string value) =>
        RouteId.Parse(value, OrderErrors.NotFound);
}
