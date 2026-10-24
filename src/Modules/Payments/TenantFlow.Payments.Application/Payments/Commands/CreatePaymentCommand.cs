namespace TenantFlow.Payments.Application.Payments.Commands;

/// <summary>Pay an order. The amount is the order's; the client sends only the provider's payment-method token.</summary>
public sealed record CreatePaymentCommand(Guid OrderId, string? IdempotencyKey, string? PaymentMethod);
