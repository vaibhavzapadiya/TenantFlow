namespace TenantFlow.Orders.Contracts;

/// <summary>What a payment for an order charges: the order's own total, never an amount the client sent.</summary>
public sealed record PayableOrder(Guid OrderId, decimal Amount, string Currency);
