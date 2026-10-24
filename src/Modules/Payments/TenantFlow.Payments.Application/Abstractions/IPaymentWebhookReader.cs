namespace TenantFlow.Payments.Application.Abstractions;

/// <summary>
/// Verifies and reads one provider's webhooks (ADR-0022 decision 5). An adapter checks the signature and timestamp before
/// it parses anything, and keeps the provider's event model to itself.
/// </summary>
public interface IPaymentWebhookReader
{
    /// <summary>The provider's stable name, as in the webhook route (<c>simulator</c> or <c>stripe</c>).</summary>
    string Provider { get; }

    /// <summary>The request header that carries the signature.</summary>
    string SignatureHeader { get; }

    /// <summary>
    /// The verified event, or null for a correctly signed event TenantFlow does not act on (another type, or not one of its
    /// payments).
    /// </summary>
    /// <param name="signature">The signature header's value, or null when it is missing.</param>
    /// <param name="now">The time the timestamp tolerance is measured from.</param>
    /// <param name="body">The request body exactly as received: the signature covers these bytes.</param>
    /// <exception cref="WebhookRejectedException">The signature or timestamp does not verify, or the body is malformed.</exception>
    ProviderWebhookEvent? Read(string? signature, ReadOnlyMemory<byte> body, DateTimeOffset now);
}
