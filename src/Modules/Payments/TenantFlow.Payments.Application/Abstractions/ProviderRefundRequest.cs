namespace TenantFlow.Payments.Application.Abstractions;

/// <summary>
/// What an adapter sends to refund: the refund (its id is the idempotency key and goes into the provider's metadata with the
/// tenant id, so a signed webhook names them back), the payment it refunds, and the amount in the payment's currency.
/// </summary>
public sealed record ProviderRefundRequest(Guid RefundId, Guid TenantId, Guid PaymentId, string? ProviderPaymentId, string PaymentMethod, decimal Amount, string Currency);

/// <summary>Which refund to ask the provider about: by its provider id when known, otherwise by TenantFlow's refund id.</summary>
public sealed record ProviderRefundQuery(Guid RefundId, Guid TenantId, string? ProviderPaymentId, string? ProviderRefundId, string PaymentMethod);

/// <summary>
/// The provider's answer about a refund in domain terms: <c>Succeeded</c>, <c>Failed</c> (with a short
/// <see cref="FailureCode"/>) or <c>Pending</c>.
/// </summary>
public sealed record ProviderRefundResult(TenantFlow.Payments.Domain.Payments.PaymentTrigger Outcome, string? ProviderRefundId, string? FailureCode = null);
