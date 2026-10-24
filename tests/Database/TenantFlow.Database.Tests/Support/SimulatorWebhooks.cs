using System.Text.Json;
using TenantFlow.Payments.Infrastructure.Providers;

namespace TenantFlow.Database.Tests.Support;

/// <summary>
/// Builds and signs simulator webhooks the way the provider would (SPEC-0006 criterion 14): Stripe's event shape and
/// signature scheme, so tests can deliver duplicate, delayed, out-of-order, forged and stale events on demand.
/// The secret is a test placeholder, not a credential.
/// </summary>
internal static class SimulatorWebhooks
{
    public const string Secret = "test-only-simulator-webhook-secret";
    public const string SignatureHeader = "TenantFlow-Simulator-Signature";
    public const string Path = "/v1/payment-webhooks/simulator";

    /// <summary>The host setting that gives the API this secret.</summary>
    public static readonly IReadOnlyDictionary<string, string> Settings = new Dictionary<string, string>
    {
        ["Payments:Simulator:WebhookSecret"] = Secret,
    };

    /// <summary>The simulator's id for a payment (it derives it from the payment id).</summary>
    public static string ProviderPaymentId(Guid paymentId) => $"sim_pi_{paymentId:N}";

    /// <summary>
    /// An event body. <paramref name="type"/> is for example <c>payment_intent.succeeded</c>, <c>payment_intent.payment_failed</c>
    /// or <c>payment_intent.processing</c>; the provider payment id defaults to the simulator's own for the payment.
    /// </summary>
    public static string Event(string eventId, string type, Guid tenantId, Guid paymentId, string? providerPaymentId = null, string? failureCode = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = eventId,
            ["object"] = "event",
            ["type"] = type,
            ["data"] = new Dictionary<string, object?>
            {
                ["object"] = new Dictionary<string, object?>
                {
                    ["id"] = providerPaymentId ?? ProviderPaymentId(paymentId),
                    ["object"] = "payment_intent",
                    ["metadata"] = new Dictionary<string, string> { ["tenant_id"] = tenantId.ToString(), ["payment_id"] = paymentId.ToString() },
                    ["last_payment_error"] = failureCode is null ? null : new Dictionary<string, string> { ["code"] = failureCode },
                },
            },
        });

    /// <summary>A refund event body (SPEC-0007 criterion 12): <c>refund.updated</c> or <c>refund.failed</c> with the refund's status.</summary>
    public static string RefundEvent(string eventId, string type, Guid tenantId, Guid refundId, string status, string? providerRefundId = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = eventId,
            ["object"] = "event",
            ["type"] = type,
            ["data"] = new Dictionary<string, object?>
            {
                ["object"] = new Dictionary<string, object?>
                {
                    ["id"] = providerRefundId ?? $"sim_re_{refundId:N}",
                    ["object"] = "refund",
                    ["status"] = status,
                    ["metadata"] = new Dictionary<string, string> { ["tenant_id"] = tenantId.ToString(), ["refund_id"] = refundId.ToString() },
                },
            },
        });

    public static string Sign(string body, DateTimeOffset signedAt, string secret = Secret) => WebhookSignature.Create(secret, body, signedAt);
}
