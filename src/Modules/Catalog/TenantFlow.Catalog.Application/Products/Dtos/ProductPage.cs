namespace TenantFlow.Catalog.Application.Products.Dtos;

/// <summary>One page of products and the cursor of the next one, or null on the last page.</summary>
public sealed record ProductPage(IReadOnlyList<ProductDetails> Items, string? NextCursor);
