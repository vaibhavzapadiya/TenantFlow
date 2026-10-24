using Microsoft.Extensions.Options;
using TenantFlow.Payments.Application.Abstractions;

namespace TenantFlow.Payments.Infrastructure.Providers.Stripe;

/// <summary>
/// Stripe's webhooks (<c>Stripe-Signature</c>, ADR-0022 decision 5): verified with the endpoint's signing secret, then
/// read as payment-intent events. Without a secret every Stripe webhook is refused (fail closed).
/// </summary>
internal sealed class StripeWebhookReader(IOptions<StripeOptions> options) : IPaymentWebhookReader
{
    public string Provider => StripePaymentProvider.ProviderName;

    public string SignatureHeader => "Stripe-Signature";

    public ProviderWebhookEvent? Read(string? signature, ReadOnlyMemory<byte> body, DateTimeOffset now)
    {
        WebhookSignature.Verify(options.Value.WebhookSecret, signature, body.Span, now);
        return PaymentIntentEvents.Parse(body);
    }
}
