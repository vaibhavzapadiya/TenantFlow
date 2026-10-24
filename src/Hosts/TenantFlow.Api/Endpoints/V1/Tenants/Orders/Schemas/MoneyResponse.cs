namespace TenantFlow.Api.Endpoints.V1.Tenants.Orders.Schemas;

/// <summary>An amount of money as the API returns it: a decimal amount and an ISO 4217 currency.</summary>
internal sealed record MoneyResponse(decimal Amount, string Currency);
