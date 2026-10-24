using TenantFlow.Catalog.Application.Abstractions;
using TenantFlow.Catalog.Contracts;
using TenantFlow.Catalog.Domain.Products;

namespace TenantFlow.Catalog.Infrastructure;

/// <summary>The Catalog module's answer to <see cref="IProductCatalog"/>: existence and plain snapshots; no Catalog type leaves the module.</summary>
internal sealed class ProductCatalog(ICatalogStore store) : IProductCatalog
{
    public Task<bool> ExistsAsync(Guid productId, CancellationToken cancellationToken) =>
        store.ExistsAsync(productId, cancellationToken);

    public async Task<IReadOnlyList<ProductSnapshot>> GetSnapshotsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(productIds);
        var products = await store.GetManyAsync(productIds, cancellationToken);
        return [.. products.Select(static p => new ProductSnapshot(p.Id, p.Sku, p.Name, p.PriceAmount, p.PriceCurrency, p.Status == ProductStatus.Active))];
    }
}
