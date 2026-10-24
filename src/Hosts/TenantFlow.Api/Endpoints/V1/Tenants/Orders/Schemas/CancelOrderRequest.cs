namespace TenantFlow.Api.Endpoints.V1.Tenants.Orders.Schemas;

/// <summary>Why an order is cancelled: required when the order is paid (it is refunded), ignored otherwise.</summary>
internal sealed record CancelOrderRequest(string? Reason = null);
