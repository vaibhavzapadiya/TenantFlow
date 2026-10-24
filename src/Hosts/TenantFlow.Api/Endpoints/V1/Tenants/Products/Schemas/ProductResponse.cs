using TenantFlow.Catalog.Application.Products.Dtos;

namespace TenantFlow.Api.Endpoints.V1.Tenants.Products.Schemas;

/// <summary>A product as the API returns it; its version travels in the <c>ETag</c> header, not the body.</summary>
internal sealed record ProductResponse(Guid Id, string Sku, string Name, PriceResponse Price, string Status)
{
    public static ProductResponse From(ProductDetails product) =>
        new(product.Id, product.Sku, product.Name, new PriceResponse(product.PriceAmount, product.PriceCurrency), product.Status);
}
