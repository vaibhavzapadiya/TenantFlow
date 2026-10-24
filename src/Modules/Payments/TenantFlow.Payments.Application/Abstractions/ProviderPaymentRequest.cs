namespace TenantFlow.Payments.Application.Abstractions;

/// <summary>
/// What an adapter sends to its provider. The tenant and payment ids go into the provider's metadata, so a signed webhook
/// can name them back (ADR-0022 decision 5).
/// </summary>
public sealed record ProviderPaymentRequest(Guid PaymentId, Guid TenantId, decimal Amount, string Currency, string PaymentMethod);
