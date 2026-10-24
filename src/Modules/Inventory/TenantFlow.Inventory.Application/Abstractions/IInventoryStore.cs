using TenantFlow.Inventory.Domain.Stock;

namespace TenantFlow.Inventory.Application.Abstractions;

/// <summary>
/// Storage for the Inventory use cases. Every method acts on the tenant in the current tenant context: it takes no tenant
/// id, filters by it, and runs in a transaction that sets it for row-level security (ADR-0011).
/// </summary>
public interface IInventoryStore
{
    /// <summary>Units on hand and reserved; <see cref="StockLevel.None"/> for a product that has never been adjusted.</summary>
    Task<StockLevel> GetLevelAsync(Guid productId, CancellationToken cancellationToken);

    /// <summary>
    /// Applies the adjustment and appends it, atomically, only if on-hand stays at or above what is reserved (so never
    /// below zero). Returns the new level, or null when the stock is insufficient (nothing is written then).
    /// </summary>
    Task<StockLevel?> TryApplyAsync(StockAdjustment adjustment, CancellationToken cancellationToken);
}
