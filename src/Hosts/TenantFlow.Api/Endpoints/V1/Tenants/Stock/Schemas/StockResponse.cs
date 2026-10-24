using TenantFlow.Inventory.Application.Stock.Dtos;

namespace TenantFlow.Api.Endpoints.V1.Tenants.Stock.Schemas;

/// <summary>A product's stock as the API returns it. <c>reserved</c> and <c>available</c> were added in SPEC-0004 (additive).</summary>
internal sealed record StockResponse(Guid ProductId, long OnHand, long Reserved, long Available)
{
    public static StockResponse From(StockDetails stock) => new(stock.ProductId, stock.OnHand, stock.Reserved, stock.Available);
}
