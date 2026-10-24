namespace TenantFlow.Api.Endpoints.V1.Tenants.Orders.Schemas;

/// <summary>An order line as the API returns it: the product as it was when the line was set.</summary>
internal sealed record OrderLineResponse(Guid ProductId, string Sku, string Name, MoneyResponse UnitPrice, int Quantity, MoneyResponse LineTotal);
