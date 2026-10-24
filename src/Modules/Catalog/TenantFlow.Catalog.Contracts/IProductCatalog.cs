namespace TenantFlow.Catalog.Contracts;

/// <summary>
/// What other modules may ask the Catalog module (ADR-0002). Inventory uses it to refuse stock for a product that does
/// not exist, and Orders to snapshot products onto order lines; neither reads the catalog schema. Every answer is for the tenant in the current tenant context only, so
/// another tenant's product is simply absent.
/// </summary>
public interface IProductCatalog
{
    /// <summary>True when the product exists in the current tenant, whatever its publication status.</summary>
    Task<bool> ExistsAsync(Guid productId, CancellationToken cancellationToken);

    /// <summary>
    /// The current tenant's products among <paramref name="productIds"/>, whatever their status, in no particular order.
    /// An id that is unknown, or another tenant's, is simply missing from the answer.
    /// </summary>
    Task<IReadOnlyList<ProductSnapshot>> GetSnapshotsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken);
}
