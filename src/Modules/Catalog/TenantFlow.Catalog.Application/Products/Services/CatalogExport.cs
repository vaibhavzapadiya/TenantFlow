using TenantFlow.Catalog.Application.Abstractions;
using TenantFlow.Catalog.Contracts;
using TenantFlow.Catalog.Domain.Products;

namespace TenantFlow.Catalog.Application.Products.Services;

/// <summary>
/// Pages of products for an export (SPEC-0008 criterion 7): the product list's own keyset query, with no status filter, so
/// archived products are exported too (exports require <c>catalog.manage</c>).
/// </summary>
public sealed class CatalogExport(ICatalogStore store) : ICatalogExport
{
    public async Task<IReadOnlyList<ProductExportRow>> ReadPageAsync(ProductExportCursor? after, int limit, CancellationToken cancellationToken)
    {
        var cursor = after is null ? null : new ProductCursor(after.Sku, after.Id);
        var page = await store.ListAsync(null, cursor, limit, null, cancellationToken);
        return [.. page.Select(static r => new ProductExportRow(r.Product.Id, r.Product.Sku, r.Product.Name, r.Product.PriceAmount, r.Product.PriceCurrency, r.Product.Status.ToName()))];
    }
}
