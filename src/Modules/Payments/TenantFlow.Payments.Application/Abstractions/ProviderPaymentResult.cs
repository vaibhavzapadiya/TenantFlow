using TenantFlow.Payments.Domain.Payments;

namespace TenantFlow.Payments.Application.Abstractions;

/// <summary>
/// The provider's answer in domain terms: <see cref="PaymentTrigger.Succeeded"/>, <see cref="PaymentTrigger.Failed"/>
/// (with a short <see cref="FailureCode"/>) or <see cref="PaymentTrigger.Pending"/>. <see cref="ProviderPaymentId"/> is null
/// when the provider refused before creating anything, so a later webhook with the provider's real id still matches.
/// </summary>
public sealed record ProviderPaymentResult(PaymentTrigger Outcome, string? ProviderPaymentId, string? FailureCode = null);
