namespace TenantFlow.Orders.Domain.Orders;

/// <summary>
/// A line as the caller asked for it, with the product's catalog data at that moment: what <see cref="Order"/> snapshots
/// so the order never changes when the catalog does (blueprint section 7).
/// </summary>
public sealed record OrderLineDraft(Guid ProductId, string Sku, string Name, decimal UnitPrice, string Currency, int Quantity);
