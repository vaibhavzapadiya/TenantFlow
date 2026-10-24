using Microsoft.Extensions.Options;
using TenantFlow.Payments.Application.Abstractions;

namespace TenantFlow.Payments.Infrastructure.Providers.Simulator;

/// <summary>
/// The simulator's webhooks: Stripe's event shape and signature scheme under the <c>TenantFlow-Simulator-Signature</c>
/// header, so the Stripe adapter reads the same events (ADR-0022 decision 1). Tests sign their own duplicate, delayed and
/// out-of-order deliveries with the configured secret.
/// </summary>
internal sealed class SimulatedWebhookReader(IOptions<SimulatorOptions> options) : IPaymentWebhookReader
{
    public const string HeaderName = "TenantFlow-Simulator-Signature";

    public string Provider => SimulatedPaymentProvider.ProviderName;

    public string SignatureHeader => HeaderName;

    public ProviderWebhookEvent? Read(string? signature, ReadOnlyMemory<byte> body, DateTimeOffset now)
    {
        WebhookSignature.Verify(options.Value.WebhookSecret, signature, body.Span, now);
        return PaymentIntentEvents.Parse(body);
    }
}
