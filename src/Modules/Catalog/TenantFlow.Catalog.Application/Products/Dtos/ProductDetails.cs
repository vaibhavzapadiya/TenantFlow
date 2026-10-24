using TenantFlow.Catalog.Application.Abstractions;
using TenantFlow.Catalog.Domain.Products;

namespace TenantFlow.Catalog.Application.Products.Dtos;

/// <summary>A product as the use cases return it: the status by its stable name, and the row version for the ETag.</summary>
public sealed record ProductDetails(Guid Id, string Sku, string Name, decimal PriceAmount, string PriceCurrency, string Status, uint Version)
{
    internal static ProductDetails From(ProductRecord record) => new(
        record.Product.Id, record.Product.Sku, record.Product.Name, record.Product.PriceAmount,
        record.Product.PriceCurrency, record.Product.Status.ToName(), record.Version);
}
