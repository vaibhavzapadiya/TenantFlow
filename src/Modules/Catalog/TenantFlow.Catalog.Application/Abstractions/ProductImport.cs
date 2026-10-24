namespace TenantFlow.Catalog.Application.Abstractions;

/// <summary>An imported product row that passed every product rule.</summary>
public sealed record ProductImport(string Sku, string Name, decimal PriceAmount, string PriceCurrency, string Status);
