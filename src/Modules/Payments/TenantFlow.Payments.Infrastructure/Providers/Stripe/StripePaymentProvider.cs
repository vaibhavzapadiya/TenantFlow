using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TenantFlow.Payments.Application.Abstractions;
using TenantFlow.Payments.Domain.Payments;

namespace TenantFlow.Payments.Infrastructure.Providers.Stripe;

/// <summary>
/// Stripe in test mode, over its REST API with no SDK (ADR-0022 decision 1). A payment is one PaymentIntent, created and
/// confirmed server-side with the customer's test payment-method token (for example <c>pm_card_visa</c>) and Stripe's
/// <c>Idempotency-Key</c> set to TenantFlow's payment id, so a repeat never charges twice. TenantFlow's tenant and payment
/// ids go into the intent's metadata, where signed webhooks and the status search find them. A refund is one Refund of the
/// intent (<c>POST /v1/refunds</c>) with the refund id as <c>Idempotency-Key</c> and in its metadata (SPEC-0007). Stripe's
/// request and response shapes stay here; the port sees domain outcomes only.
/// <list type="bullet">
/// <item>200: <c>succeeded</c> is succeeded; <c>requires_action</c> (3-D Secure, which a server-side or renewal charge cannot
/// complete) is failed with code <c>requires_action</c> (SPEC-0007 criterion 16); <c>processing</c> and the like are pending
/// (reconciled).</item>
/// <item>402 card error or 400 invalid request: failed, with Stripe's code; nothing was charged.</item>
/// <item>401, 429, 5xx, a timeout, a network error or an unreadable body: unknown (<see cref="PaymentProviderUnavailableException"/>).</item>
/// </list>
/// </summary>
internal sealed class StripePaymentProvider(StripeTransport transport, IOptions<StripeOptions> options) : IPaymentProvider
{
    public const string ProviderName = "stripe";

    private const string RequiresAction = "requires_action";

    public string Name => ProviderName;

    public async Task<ProviderPaymentResult> CreateAsync(ProviderPaymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (StripeAmounts.ToMinorUnits(request.Amount, request.Currency) is not { } amount)
        {
            // Definitive and nothing was sent: Stripe cannot charge exactly this amount in this currency.
            return new ProviderPaymentResult(PaymentTrigger.Failed, null, "amount_not_representable");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/payment_intents")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["amount"] = amount.ToString(CultureInfo.InvariantCulture),
                ["currency"] = request.Currency.ToLowerInvariant(),
                ["payment_method"] = request.PaymentMethod,
                ["confirm"] = "true",
                // Server-side confirmation only: no redirect-based method can be chosen (no 3-D Secure flow in M6).
                ["automatic_payment_methods[enabled]"] = "true",
                ["automatic_payment_methods[allow_redirects]"] = "never",
                [$"metadata[{PaymentIntentEvents.TenantIdKey}]"] = request.TenantId.ToString(),
                [$"metadata[{PaymentIntentEvents.PaymentIdKey}]"] = request.PaymentId.ToString(),
            }),
        };
        message.Headers.Add("Idempotency-Key", request.PaymentId.ToString());
        using var response = await SendAsync(message, cancellationToken);
        using var json = await ReadAsync(response, cancellationToken);
        var root = json.RootElement;
        if (response.IsSuccessStatusCode)
        {
            return await SettleAsync(root, cancellationToken);
        }

        if (response.StatusCode is HttpStatusCode.PaymentRequired or HttpStatusCode.BadRequest
            && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var intentId = error.TryGetProperty("payment_intent", out var intent) && intent.ValueKind == JsonValueKind.Object ? Text(intent, "id") : null;
            var code = Text(error, "decline_code") ?? Text(error, "code") ?? Text(error, "type") ?? "declined";
            // No intent id: Stripe created nothing to refer to, and a later webhook with a real id must still match.
            return new ProviderPaymentResult(PaymentTrigger.Failed, intentId, Truncate(code));
        }

        throw new PaymentProviderUnavailableException($"Stripe answered {(int)response.StatusCode}.");
    }

    public async Task<ProviderPaymentResult?> GetStatusAsync(ProviderStatusQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        JsonElement intent;
        if (query.ProviderPaymentId is { } id && id.StartsWith("pi_", StringComparison.Ordinal))
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, $"v1/payment_intents/{Uri.EscapeDataString(id)}");
            using var response = await SendAsync(message, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            using var json = await ReadSuccessAsync(response, cancellationToken);
            intent = json.RootElement.Clone();
        }
        else
        {
            // The create call's answer was lost: find the intent by the payment id in its metadata. Stripe's search is
            // eventually consistent, so "not found" here means "not known yet", which the caller counts as unknown.
            var search = $"metadata['{PaymentIntentEvents.PaymentIdKey}']:'{query.PaymentId}'";
            using var message = new HttpRequestMessage(HttpMethod.Get, $"v1/payment_intents/search?query={Uri.EscapeDataString(search)}&limit=1");
            using var response = await SendAsync(message, cancellationToken);
            using var json = await ReadSuccessAsync(response, cancellationToken);
            if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            {
                return null;
            }

            intent = data[0].Clone();
        }

        return await SettleAsync(intent, cancellationToken);
    }

    // An intent that requires action (3-D Secure) cannot be completed by anyone here: it is cancelled before it is reported
    // failed, so it can never succeed later behind a failed payment (review fix R10). If the cancel gets no clear answer,
    // the payment stays pending and reconciliation tries again.
    private async Task<ProviderPaymentResult> SettleAsync(JsonElement intent, CancellationToken cancellationToken)
    {
        var result = Outcome(intent);
        if (result.FailureCode != RequiresAction || result.ProviderPaymentId is not { } id)
        {
            return result;
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, $"v1/payment_intents/{Uri.EscapeDataString(id)}/cancel")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["cancellation_reason"] = "abandoned" }),
        };
        try
        {
            using var response = await SendAsync(message, cancellationToken);
            using var json = await ReadAsync(response, cancellationToken);
            return response.IsSuccessStatusCode && Text(json.RootElement, "status") == "canceled" ? result : new ProviderPaymentResult(PaymentTrigger.Pending, id);
        }
        catch (PaymentProviderUnavailableException)
        {
            return new ProviderPaymentResult(PaymentTrigger.Pending, id);
        }
    }

    public async Task<ProviderRefundResult> RefundAsync(ProviderRefundRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProviderPaymentId is not { } intent || StripeAmounts.ToMinorUnits(request.Amount, request.Currency) is not { } amount)
        {
            // Definitive and nothing was sent: no intent to refund, or an amount Stripe cannot refund exactly.
            return new ProviderRefundResult(PaymentTrigger.Failed, null, "refund_not_representable");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/refunds")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["payment_intent"] = intent,
                ["amount"] = amount.ToString(CultureInfo.InvariantCulture),
                [$"metadata[{PaymentIntentEvents.TenantIdKey}]"] = request.TenantId.ToString(),
                [$"metadata[{PaymentIntentEvents.RefundIdKey}]"] = request.RefundId.ToString(),
            }),
        };
        message.Headers.Add("Idempotency-Key", request.RefundId.ToString());
        using var response = await SendAsync(message, cancellationToken);
        using var json = await ReadAsync(response, cancellationToken);
        var root = json.RootElement;
        if (response.IsSuccessStatusCode)
        {
            return RefundOutcome(root);
        }

        if (response.StatusCode == HttpStatusCode.BadRequest && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = Text(error, "code") ?? Text(error, "type") ?? "refund_refused";
            if (code is "charge_already_refunded" or "amount_too_large")
            {
                // A re-send after Stripe's idempotency window can meet its own earlier success: not a failure, an unknown
                // outcome that reconciliation settles from the refund itself (review fix R7).
                throw new PaymentProviderUnavailableException("Stripe refused a refund that may already have been made.");
            }

            return new ProviderRefundResult(PaymentTrigger.Failed, null, Truncate(code));
        }

        throw new PaymentProviderUnavailableException($"Stripe answered {(int)response.StatusCode}.");
    }

    public async Task<ProviderRefundResult?> GetRefundStatusAsync(ProviderRefundQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.ProviderRefundId is { } id && id.StartsWith("re_", StringComparison.Ordinal))
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, $"v1/refunds/{Uri.EscapeDataString(id)}");
            using var response = await SendAsync(message, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            using var json = await ReadSuccessAsync(response, cancellationToken);
            return RefundOutcome(json.RootElement);
        }

        if (query.ProviderPaymentId is not { } intent)
        {
            return null;
        }

        // The create call's answer was lost: list the intent's refunds, page by page, and find this one by its metadata.
        string? after = null;
        while (true)
        {
            var path = $"v1/refunds?payment_intent={Uri.EscapeDataString(intent)}&limit=100" + (after is null ? string.Empty : $"&starting_after={Uri.EscapeDataString(after)}");
            using var list = new HttpRequestMessage(HttpMethod.Get, path);
            using var listed = await SendAsync(list, cancellationToken);
            using var page = await ReadSuccessAsync(listed, cancellationToken);
            if (!page.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            {
                return null;
            }

            foreach (var refund in data.EnumerateArray())
            {
                if (refund.TryGetProperty("metadata", out var metadata) && Text(metadata, PaymentIntentEvents.RefundIdKey) == query.RefundId.ToString())
                {
                    return RefundOutcome(refund);
                }
            }

            if (!(page.RootElement.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True))
            {
                return null;
            }

            after = Text(data[data.GetArrayLength() - 1], "id");
        }
    }

    // A Refund in domain terms.
    private static ProviderRefundResult RefundOutcome(JsonElement refund)
    {
        var id = Text(refund, "id") ?? throw new PaymentProviderUnavailableException("Stripe answered without a refund id.");
        var outcome = PaymentIntentEvents.RefundOutcome(Text(refund, "status"));
        return new ProviderRefundResult(outcome, id, outcome == PaymentTrigger.Failed ? Truncate(Text(refund, "failure_reason") ?? "refund_failed") : null);
    }

    // A PaymentIntent in domain terms.
    private static ProviderPaymentResult Outcome(JsonElement intent)
    {
        var id = Text(intent, "id") ?? throw new PaymentProviderUnavailableException("Stripe answered without an intent id.");
        return Text(intent, "status") switch
        {
            "succeeded" => new ProviderPaymentResult(PaymentTrigger.Succeeded, id),
            // A confirmed intent back in requires_payment_method was declined.
            "requires_payment_method" or "canceled" => new ProviderPaymentResult(PaymentTrigger.Failed, id, Truncate(LastErrorCode(intent) ?? "declined")),
            // No customer is present to authenticate: waiting would leave the payment in review forever (M6 finding).
            "requires_action" => new ProviderPaymentResult(PaymentTrigger.Failed, id, RequiresAction),
            _ => new ProviderPaymentResult(PaymentTrigger.Pending, id),
        };
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.SecretKey);
        try
        {
            return await transport.Client.SendAsync(message, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // A timeout or a broken connection: Stripe may or may not have acted. The message never includes the request.
            throw new PaymentProviderUnavailableException("Stripe could not be reached in time.", exception);
        }
    }

    private static async Task<JsonDocument> ReadSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        response.IsSuccessStatusCode
            ? await ReadAsync(response, cancellationToken)
            : throw new PaymentProviderUnavailableException($"Stripe answered {(int)response.StatusCode}.");

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new PaymentProviderUnavailableException("Stripe answered with an unreadable body.", exception);
        }
    }

    private static string? LastErrorCode(JsonElement intent) =>
        intent.TryGetProperty("last_payment_error", out var error) && error.ValueKind == JsonValueKind.Object
            ? Text(error, "decline_code") ?? Text(error, "code")
            : null;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Truncate(string code) => code.Length <= 64 ? code : code[..64];
}
