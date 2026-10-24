namespace TenantFlow.Api.Endpoints.V1.Tenants.Products.Schemas;

/// <summary>A price as the API returns it.</summary>
internal sealed record PriceResponse(decimal Amount, string Currency);
