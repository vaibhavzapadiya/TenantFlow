namespace TenantFlow.Inventory.Domain.Stock;

/// <summary>A product's stock at one moment: what is on hand, what orders hold, and what is left to promise.</summary>
public readonly record struct StockLevel(long OnHand, long Reserved)
{
    public static readonly StockLevel None = new(0, 0);

    public long Available => OnHand - Reserved;
}
