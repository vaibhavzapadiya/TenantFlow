namespace TenantFlow.Api.Endpoints.V1.Tenants.Orders.Schemas;

/// <summary>One requested line of an order.</summary>
internal sealed record OrderLineRequest(Guid? ProductId, int? Quantity);
