namespace TenantFlow.Api.Endpoints.V1.Tenants.Products.Schemas;

/// <summary>A price as a client sends it: a decimal amount and an ISO 4217 currency. Never a float (AGENTS.md rule 4).</summary>
internal sealed record PriceRequest(decimal? Amount, string? Currency);
