using TenantFlow.Api.Endpoints.V1.TenantAccess;
using TenantFlow.Api.Endpoints.V1.Tenants.Products;
using TenantFlow.Api.Endpoints.V1.Tenants.Stock.Schemas;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.Inventory.Application.Stock.Commands;
using TenantFlow.Inventory.Application.Stock.Services;

namespace TenantFlow.Api.Endpoints.V1.Tenants.Stock;

/// <summary>
/// A product's stock (SPEC-0003 criteria 11 to 16). Each route declares its permission through
/// <c>RequireTenantPermission</c>; handlers act on the tenant context, never on the route's text. Failures (ADR-0016):
/// 400 invalid delta or reason; 404 <c>catalog.product_not_found</c> for an unknown or another tenant's product;
/// 409 <c>inventory.insufficient_stock</c> when the balance would go negative.
/// </summary>
internal static class StockEndpoints
{
    public static IEndpointRouteBuilder MapStockEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var stock = endpoints.MapGroup("/tenants/{tenantId}/products/{productId}/stock")
            .WithTags("Stock");

        stock.MapGet("/", static async (string productId, StockService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(StockResponse.From(await service.GetAsync(ProductRouteId.Parse(productId), cancellationToken))))
            .RequireTenantPermission(TenantPermissions.InventoryRead);

        stock.MapPost("/adjustments", static async (string productId, AdjustStockRequest request, StockService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(StockResponse.From(await service.AdjustAsync(
                    ProductRouteId.Parse(productId), new AdjustStockCommand(request.Delta, request.Reason), cancellationToken))))
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireTenantPermission(TenantPermissions.InventoryManage);

        return endpoints;
    }
}
