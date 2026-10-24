namespace TenantFlow.Api.Endpoints.V1.Tenants.Products.Schemas;

/// <summary>Body of <c>PUT /tenants/{tenantId}/products/{productId}</c>. The SKU is not editable.</summary>
internal sealed record UpdateProductRequest(string? Name, PriceRequest? Price, string? Status);
