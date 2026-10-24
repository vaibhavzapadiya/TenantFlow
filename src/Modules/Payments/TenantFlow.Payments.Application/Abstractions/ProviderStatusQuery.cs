namespace TenantFlow.Payments.Application.Abstractions;

/// <summary>
/// Which payment to ask the provider about: by its provider id when known, otherwise by TenantFlow's payment id (the
/// idempotency key and metadata it was created with).
/// </summary>
public sealed record ProviderStatusQuery(Guid PaymentId, Guid TenantId, string? ProviderPaymentId, string PaymentMethod);
