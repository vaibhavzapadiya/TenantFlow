using System.Text;
using System.Text.Json;
using TenantFlow.Payments.Application.Abstractions;
using TenantFlow.Payments.Domain.Payments;

namespace TenantFlow.Payments.Infrastructure.Providers;

/// <summary>
/// Reads a Stripe-shaped payment-intent or refund event (the simulator sends the same shapes): <c>id</c>, <c>type</c> and
/// <c>data.object</c> with <c>id</c>, <c>metadata.tenant_id</c> and <c>metadata.payment_id</c> (a payment intent; on failure
/// <c>last_payment_error.code</c>) or <c>metadata.refund_id</c> (a refund, SPEC-0007 criterion 12: <c>refund.updated</c> and
/// <c>refund.failed</c> by its <c>status</c>, with <c>failure_reason</c>). Only the types TenantFlow acts on are mapped; any
/// other correctly signed event, and
/// one without TenantFlow's metadata (created elsewhere on the same provider account), is null: acknowledged and ignored,
/// so the provider does not retry it for days or disable the endpoint. Call it only after the signature verified.
/// </summary>
internal static class PaymentIntentEvents
{
    /// <summary>The metadata keys TenantFlow sets on every provider payment.</summary>
    public const string TenantIdKey = "tenant_id";
    public const string PaymentIdKey = "payment_id";
    public const string RefundIdKey = "refund_id";

    /// <exception cref="WebhookRejectedException"><see cref="WebhookRejection.Malformed"/>: not JSON, or a mapped event without its fields.</exception>
    public static ProviderWebhookEvent? Parse(ReadOnlyMemory<byte> body)
    {
        try
        {
            // A UTF-8 byte-order mark is signed like any other byte, but is not JSON: skip it only once verified.
            var utf8Bom = Encoding.UTF8.Preamble;
            using var json = JsonDocument.Parse(body.Span.StartsWith(utf8Bom) ? body[utf8Bom.Length..] : body);
            var root = json.RootElement;
            var type = Text(root, "type");
            if (type is "refund.updated" or "refund.failed")
            {
                return ParseRefund(root);
            }

            var outcome = type switch
            {
                "payment_intent.succeeded" => PaymentTrigger.Succeeded,
                "payment_intent.payment_failed" => PaymentTrigger.Failed,
                "payment_intent.processing" => PaymentTrigger.Pending,
                _ => (PaymentTrigger?)null,
            };
            if (outcome is null)
            {
                return null;
            }

            var intent = root.GetProperty("data").GetProperty("object");
            if (!intent.TryGetProperty("metadata", out var metadata) || Text(metadata, TenantIdKey) is null || Text(metadata, PaymentIdKey) is null)
            {
                return null;
            }

            var failure = intent.TryGetProperty("last_payment_error", out var error) && error.ValueKind == JsonValueKind.Object ? Text(error, "code") : null;
            return new ProviderWebhookEvent(
                Required(Text(root, "id")),
                Guid.Parse(Required(Text(metadata, TenantIdKey))),
                Guid.Parse(Required(Text(metadata, PaymentIdKey))),
                Required(Text(intent, "id")),
                outcome.Value,
                outcome == PaymentTrigger.Failed ? Truncate(failure ?? "unknown") : null);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // The parser's message can quote body text, so the inner exception is not kept; the reason stays generic.
            throw new WebhookRejectedException(WebhookRejection.Malformed);
        }
    }

    // A refund event: its status decides; one without TenantFlow's metadata is not ours.
    private static ProviderWebhookEvent? ParseRefund(JsonElement root)
    {
        var refund = root.GetProperty("data").GetProperty("object");
        if (!refund.TryGetProperty("metadata", out var metadata) || Text(metadata, TenantIdKey) is null || Text(metadata, RefundIdKey) is null)
        {
            return null;
        }

        var outcome = RefundOutcome(Text(refund, "status"));
        return new ProviderWebhookEvent(
            Required(Text(root, "id")),
            Guid.Parse(Required(Text(metadata, TenantIdKey))),
            Guid.Parse(Required(Text(metadata, RefundIdKey))),
            Required(Text(refund, "id")),
            outcome,
            outcome == PaymentTrigger.Failed ? Truncate(Text(refund, "failure_reason") ?? "unknown") : null,
            IsRefund: true);
    }

    /// <summary>A Stripe refund status in domain terms: <c>succeeded</c>, <c>failed</c> or <c>canceled</c> are final; the rest pending.</summary>
    public static PaymentTrigger RefundOutcome(string? status) => status switch
    {
        "succeeded" => PaymentTrigger.Succeeded,
        "failed" or "canceled" => PaymentTrigger.Failed,
        _ => PaymentTrigger.Pending,
    };

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Required(string? value) =>
        value is { Length: > 0 and <= 255 } ? value : throw new FormatException("A required field is missing.");

    // The failure code column is 64 characters; a provider's code is short, but never trusted to be.
    private static string Truncate(string code) => code.Length <= 64 ? code : code[..64];
}
