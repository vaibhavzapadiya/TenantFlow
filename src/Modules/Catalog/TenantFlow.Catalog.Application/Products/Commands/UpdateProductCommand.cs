using TenantFlow.Catalog.Application.Products.Validators;

namespace TenantFlow.Catalog.Application.Products.Commands;

/// <summary>Input of the update-product use case, validated by <see cref="UpdateProductCommandValidator"/>.</summary>
public sealed record UpdateProductCommand(string? Name, decimal? PriceAmount, string? PriceCurrency, string? Status);
