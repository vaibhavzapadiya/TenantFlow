namespace TenantFlow.Inventory.Application.Stock.Dtos;

/// <summary>A product's stock as the use cases return it: on hand, held by orders, and left to promise.</summary>
public sealed record StockDetails(Guid ProductId, long OnHand, long Reserved, long Available);
