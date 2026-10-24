using TenantFlow.Payments.Domain.Payments;

namespace TenantFlow.Payments.Application.Abstractions;

/// <summary>
/// A verified webhook in domain terms. <see cref="TenantId"/> and <see cref="SubjectId"/> come from the metadata TenantFlow
/// put on the provider's object; they select a payment, or with <see cref="IsRefund"/> a refund (SPEC-0007 criterion 12),
/// and the stored provider reference must still match <see cref="ProviderReference"/>.
/// </summary>
public sealed record ProviderWebhookEvent(string EventId, Guid TenantId, Guid SubjectId, string ProviderReference, PaymentTrigger Outcome, string? FailureCode, bool IsRefund = false);
