using TenantFlow.Catalog.Application.Products.Validators;

namespace TenantFlow.Catalog.Application.Products.Commands;

/// <summary>Input of the create-product use case, validated by <see cref="CreateProductCommandValidator"/>.</summary>
public sealed record CreateProductCommand(string? Sku, string? Name, decimal? PriceAmount, string? PriceCurrency);
