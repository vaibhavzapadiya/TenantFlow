namespace TenantFlow.Payments.Application.Refunds.Commands;

/// <summary>Refund part or all of an order's payment, in the payment's currency, with a reason (audit).</summary>
public sealed record CreateRefundCommand(Guid OrderId, string? IdempotencyKey, decimal? Amount, string? Reason);
