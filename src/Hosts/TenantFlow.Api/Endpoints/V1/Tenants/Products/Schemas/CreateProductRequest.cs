namespace TenantFlow.Api.Endpoints.V1.Tenants.Products.Schemas;

/// <summary>Body of <c>POST /tenants/{tenantId}/products</c>. Rules: <c>CreateProductCommandValidator</c> (ADR-0014).</summary>
internal sealed record CreateProductRequest(string? Sku, string? Name, PriceRequest? Price);
