namespace TenantFlow.Catalog.Contracts;

/// <summary>
/// A product as another module may copy it (SPEC-0004): what an order line freezes, and whether the product can be ordered
/// now. Plain data: no Catalog type leaves the module.
/// </summary>
public sealed record ProductSnapshot(Guid ProductId, string Sku, string Name, decimal PriceAmount, string PriceCurrency, bool IsActive);
