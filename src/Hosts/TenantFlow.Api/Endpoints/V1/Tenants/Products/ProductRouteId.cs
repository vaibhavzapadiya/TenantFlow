using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.Catalog.Domain.Products;

namespace TenantFlow.Api.Endpoints.V1.Tenants.Products;

/// <summary>Reads a product id from a route: only the canonical form can name one, anything else is an unknown product.</summary>
internal static class ProductRouteId
{
    /// <exception cref="AppException"><see cref="ProductErrors.NotFound"/> when the value is not a product id.</exception>
    public static Guid Parse(string value) =>
        RouteId.Parse(value, ProductErrors.NotFound);
}
