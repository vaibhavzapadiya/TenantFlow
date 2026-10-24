namespace TenantFlow.Catalog.Contracts;

/// <summary>
/// Product exports for the ImportsExports module (SPEC-0008 criterion 7, ADR-0024 decision 2), in the tenant of the current
/// tenant context: every product, active or archived, a page at a time in (SKU, id) order. A page is a keyset step, not an
/// offset, so products added during an export never shift the pages already read.
/// </summary>
public interface ICatalogExport
{
    /// <summary>Up to <paramref name="limit"/> products strictly after <paramref name="after"/> (from the first when null).</summary>
    Task<IReadOnlyList<ProductExportRow>> ReadPageAsync(ProductExportCursor? after, int limit, CancellationToken cancellationToken);
}

/// <summary>A product as exported: the import's columns, and its id for the next page's cursor.</summary>
public sealed record ProductExportRow(Guid Id, string Sku, string Name, decimal PriceAmount, string PriceCurrency, string Status)
{
    public ProductExportCursor Cursor => new(Sku, Id);
}

/// <summary>Where the previous page ended.</summary>
public sealed record ProductExportCursor(string Sku, Guid Id);
