namespace TenantFlow.Api.Endpoints.V1.Tenants.Payments.Schemas;

/// <summary>
/// The body of a payment request: only the provider's payment-method token (for example <c>sim_success</c> or
/// <c>pm_card_visa</c>). The amount is the order's; card data is never accepted.
/// </summary>
internal sealed record CreatePaymentRequest(string? PaymentMethod);
