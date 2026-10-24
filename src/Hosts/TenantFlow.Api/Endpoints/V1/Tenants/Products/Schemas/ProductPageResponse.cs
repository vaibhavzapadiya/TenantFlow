using TenantFlow.Catalog.Application.Products.Dtos;

namespace TenantFlow.Api.Endpoints.V1.Tenants.Products.Schemas;

/// <summary>One page of products; <c>nextCursor</c> is null on the last page (ADR-0019 keyset paging).</summary>
internal sealed record ProductPageResponse(IReadOnlyList<ProductResponse> Items, string? NextCursor)
{
    public static ProductPageResponse From(ProductPage page) => new([.. page.Items.Select(ProductResponse.From)], page.NextCursor);
}
