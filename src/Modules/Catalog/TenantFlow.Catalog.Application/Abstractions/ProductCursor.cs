namespace TenantFlow.Catalog.Application.Abstractions;

/// <summary>A keyset position: the last product of the previous page, by (SKU, id).</summary>
public sealed record ProductCursor(string Sku, Guid Id);
