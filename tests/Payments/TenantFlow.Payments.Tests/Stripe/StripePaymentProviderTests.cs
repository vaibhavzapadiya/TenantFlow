using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using TenantFlow.Payments.Application.Abstractions;
using TenantFlow.Payments.Domain.Payments;
using TenantFlow.Payments.Infrastructure.Providers;
using TenantFlow.Payments.Infrastructure.Providers.Stripe;

namespace TenantFlow.Payments.Tests.Stripe;

/// <summary>
/// SPEC-0006 criterion 14 (Stripe): the test-mode adapter maps Stripe's documented request and response shapes to the
/// payment port, never charges twice, treats every ambiguous answer as unknown, and verifies <c>Stripe-Signature</c>.
/// No network: a fake handler answers with documented bodies.
/// </summary>
public sealed class StripePaymentProviderTests
{
    private const string TestKey = "sk_test_placeholder";
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid PaymentId = Guid.CreateVersion7();

    [Fact]
    public async Task A_charge_is_one_confirmed_intent_in_minor_units_keyed_by_the_payment_id_and_success_is_read_back()
    {
        var stripe = new FakeStripe().Answer(HttpStatusCode.OK, StripeFixtures.Intent("pi_1", "succeeded"));

        var result = await Provider(stripe).CreateAsync(Request(12.50m, "EUR"), TestContext.Current.CancellationToken);

        Assert.Equal(new ProviderPaymentResult(PaymentTrigger.Succeeded, "pi_1"), result);
        var sent = Assert.Single(stripe.Requests);
        Assert.Equal((HttpMethod.Post, "/v1/payment_intents", PaymentId.ToString(), $"Bearer {TestKey}"), (sent.Method, sent.PathAndQuery, sent.IdempotencyKey, sent.Authorization));
        var form = sent.Body.Split('&').Select(Uri.UnescapeDataString).ToHashSet();
        Assert.Superset(
            new HashSet<string> { "amount=1250", "currency=eur", "payment_method=pm_card_visa", "confirm=true", "automatic_payment_methods[allow_redirects]=never",
                $"metadata[tenant_id]={Tenant}", $"metadata[payment_id]={PaymentId}" },
            form);
    }

    [Fact]
    public async Task A_decline_is_a_definitive_failure_with_stripes_code_and_a_processing_intent_is_pending()
    {
        var stripe = new FakeStripe()
            .Answer(HttpStatusCode.PaymentRequired, StripeFixtures.CardDeclined("pi_2"))
            .Answer(HttpStatusCode.OK, StripeFixtures.Intent("pi_3", "processing"));

        var declined = await Provider(stripe).CreateAsync(Request(1m, "EUR"), TestContext.Current.CancellationToken);
        var processing = await Provider(stripe).CreateAsync(Request(1m, "EUR"), TestContext.Current.CancellationToken);

        Assert.Equal(new ProviderPaymentResult(PaymentTrigger.Failed, "pi_2", "insufficient_funds"), declined);
        var noIntent = await Provider(new FakeStripe().Answer(HttpStatusCode.BadRequest, StripeFixtures.RateLimited)).CreateAsync(Request(1m, "EUR"), TestContext.Current.CancellationToken);
        Assert.Equal(new ProviderPaymentResult(PaymentTrigger.Failed, null, "rate_limit"), noIntent);
        Assert.Equal(new ProviderPaymentResult(PaymentTrigger.Pending, "pi_3"), processing);
    }

    [Fact]
    public async Task Every_ambiguous_answer_is_an_unknown_outcome_and_nothing_is_sent_twice()
    {
        var stripe = new FakeStripe()
            .Answer(HttpStatusCode.InternalServerError, "{}")
            .Answer(HttpStatusCode.TooManyRequests, StripeFixtures.RateLimited)
            .Answer(HttpStatusCode.Unauthorized, "{}")
            .Answer(HttpStatusCode.OK, "not json")
            .Fail(new TaskCanceledException("timeout"))
            .Fail(new HttpRequestException("connection reset"));
        var provider = Provider(stripe);

        for (var i = 0; i < 6; i++)
        {
            await Assert.ThrowsAsync<PaymentProviderUnavailableException>(() => provider.CreateAsync(Request(1m, "EUR"), TestContext.Current.CancellationToken));
        }

        Assert.Equal(6, stripe.Requests.Count);
    }

    [Fact]
    public async Task An_amount_stripe_cannot_charge_exactly_is_refused_before_anything_is_sent()
    {
        var stripe = new FakeStripe()
            .Answer(HttpStatusCode.OK, StripeFixtures.Intent("pi_4", "succeeded"))
            .Answer(HttpStatusCode.OK, StripeFixtures.Intent("pi_5", "succeeded"));
        var provider = Provider(stripe);

        var subCent = await provider.CreateAsync(Request(12.345m, "EUR"), TestContext.Current.CancellationToken);
        var kuwaitiOdd = await provider.CreateAsync(Request(1.235m, "KWD"), TestContext.Current.CancellationToken);
        await provider.CreateAsync(Request(1000m, "JPY"), TestContext.Current.CancellationToken);
        await provider.CreateAsync(Request(1.23m, "KWD"), TestContext.Current.CancellationToken);

        Assert.Equal(("amount_not_representable", "amount_not_representable"), (subCent.FailureCode, kuwaitiOdd.FailureCode));
        Assert.Equal((null, null), (subCent.ProviderPaymentId, kuwaitiOdd.ProviderPaymentId));
        Assert.Equal([PaymentTrigger.Failed, PaymentTrigger.Failed], [subCent.Outcome, kuwaitiOdd.Outcome]);
        Assert.Equal(["amount=1000", "amount=1230"], stripe.Requests.Select(static r => r.Body.Split('&')[0]));
    }

    [Fact]
    public async Task Status_is_read_by_intent_id_or_found_by_the_payment_id_and_not_yet_found_is_null()
    {
        var stripe = new FakeStripe()
            .Answer(HttpStatusCode.OK, StripeFixtures.Intent("pi_6", "requires_payment_method", declineCode: "generic_decline"))
            .Answer(HttpStatusCode.OK, StripeFixtures.Search())
            .Answer(HttpStatusCode.OK, StripeFixtures.Search(StripeFixtures.Intent("pi_7", "succeeded")));
        var provider = Provider(stripe);

        var byId = await provider.GetStatusAsync(new ProviderStatusQuery(PaymentId, Tenant, "pi_6", "pm_card_visa"), TestContext.Current.CancellationToken);
        var notYet = await provider.GetStatusAsync(new ProviderStatusQuery(PaymentId, Tenant, null, "pm_card_visa"), TestContext.Current.CancellationToken);
        var found = await provider.GetStatusAsync(new ProviderStatusQuery(PaymentId, Tenant, null, "pm_card_visa"), TestContext.Current.CancellationToken);

        Assert.Equal(new ProviderPaymentResult(PaymentTrigger.Failed, "pi_6", "generic_decline"), byId);
        Assert.Null(notYet);
        Assert.Equal(new ProviderPaymentResult(PaymentTrigger.Succeeded, "pi_7"), found);
        Assert.Equal("/v1/payment_intents/pi_6", stripe.Requests[0].PathAndQuery);
        Assert.Contains(Uri.EscapeDataString($"metadata['payment_id']:'{PaymentId}'"), stripe.Requests[1].PathAndQuery, StringComparison.Ordinal);
        Assert.All(stripe.Requests, static r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public void A_stripe_signed_event_is_read_and_a_wrong_or_missing_secret_refuses_it()
    {
        var now = DateTimeOffset.UtcNow;
        var body = StripeFixtures.Event("evt_1", "payment_intent.payment_failed", Tenant, PaymentId, "pi_8", "card_declined");
        var reader = new StripeWebhookReader(Options.Create(new StripeOptions { WebhookSecret = "whsec_placeholder" }));
        var unconfigured = new StripeWebhookReader(Options.Create(new StripeOptions()));

        var bytes = Encoding.UTF8.GetBytes(body);
        // The exact bytes are signed, a byte-order mark included: nothing is decoded before the signature is checked.
        var withBom = Encoding.UTF8.GetPreamble().Concat(bytes).ToArray();
        var read = reader.Read(WebhookSignature.Create("whsec_placeholder", bytes, now), bytes, now);
        var readWithBom = reader.Read(WebhookSignature.Create("whsec_placeholder", withBom, now), withBom, now);
        var bomStripped = Assert.Throws<WebhookRejectedException>(() => reader.Read(WebhookSignature.Create("whsec_placeholder", withBom, now), bytes, now));
        var forged = Assert.Throws<WebhookRejectedException>(() => reader.Read(WebhookSignature.Create("whsec_other", bytes, now), bytes, now));
        var closed = Assert.Throws<WebhookRejectedException>(() => unconfigured.Read(WebhookSignature.Create("whsec_placeholder", bytes, now), bytes, now));

        var expected = new ProviderWebhookEvent("evt_1", Tenant, PaymentId, "pi_8", PaymentTrigger.Failed, "card_declined");
        Assert.Equal((expected, expected), (read, readWithBom));
        Assert.Equal(WebhookRejection.Signature, bomStripped.Reason);
        Assert.Equal(("Stripe-Signature", "stripe"), (reader.SignatureHeader, reader.Provider));
        Assert.Equal((WebhookRejection.Signature, WebhookRejection.Signature), (forged.Reason, closed.Reason));
    }

    // ---- SPEC-0007 criterion 16: folded M6 findings ----

    [Fact]
    public async Task An_intent_that_requires_action_is_a_failed_payment_not_a_pending_one()
    {
        // Review fix R10: the intent is cancelled first; if the cancel gets no clear answer the payment stays pending.
        var stripe = new FakeStripe()
            .Answer(HttpStatusCode.OK, StripeFixtures.Intent("pi_4", "requires_action"))
            .Answer(HttpStatusCode.OK, StripeFixtures.Intent("pi_4", "canceled"))
            .Answer(HttpStatusCode.OK, StripeFixtures.Intent("pi_5", "requires_action"))
            .Answer(HttpStatusCode.InternalServerError, "{}");

        var created = await Provider(stripe).CreateAsync(Request(1m, "EUR"), TestContext.Current.CancellationToken);
        var uncancelled = await Provider(stripe).GetStatusAsync(new ProviderStatusQuery(PaymentId, Tenant, "pi_5", "pm_card_visa"), TestContext.Current.CancellationToken);

        Assert.Equal(new ProviderPaymentResult(PaymentTrigger.Failed, "pi_4", "requires_action"), created);
        Assert.Equal(new ProviderPaymentResult(PaymentTrigger.Pending, "pi_5"), uncancelled);
        Assert.Equal((HttpMethod.Post, "/v1/payment_intents/pi_4/cancel"), (stripe.Requests[1].Method, stripe.Requests[1].PathAndQuery));
    }

    [Fact]
    public void Isk_and_ugx_are_whole_units_sent_in_hundredths_and_a_fraction_is_refused()
    {
        Assert.Equal(150_000L, StripeAmounts.ToMinorUnits(1500m, "UGX"));
        Assert.Equal(1_000L, StripeAmounts.ToMinorUnits(10m, "isk"));
        Assert.Null(StripeAmounts.ToMinorUnits(1500.5m, "UGX"));
        Assert.Null(StripeAmounts.ToMinorUnits(10.01m, "ISK"));
        Assert.Equal(500L, StripeAmounts.ToMinorUnits(500m, "JPY"));
        Assert.Equal(1250L, StripeAmounts.ToMinorUnits(12.50m, "EUR"));
    }

    // ---- SPEC-0007 criteria 9 and 12: refunds ----

    [Fact]
    public async Task A_refund_is_one_refund_of_the_intent_in_minor_units_keyed_by_the_refund_id()
    {
        var refundId = Guid.CreateVersion7();
        var stripe = new FakeStripe()
            .Answer(HttpStatusCode.OK, StripeFixtures.Refund("re_1", "succeeded", refundId))
            .Answer(HttpStatusCode.OK, StripeFixtures.Refund("re_2", "pending", refundId))
            .Answer(HttpStatusCode.OK, StripeFixtures.Refund("re_3", "failed", refundId, "expired_or_canceled_card"));

        var succeeded = await Provider(stripe).RefundAsync(RefundRequest(refundId, 4m), TestContext.Current.CancellationToken);
        var pending = await Provider(stripe).RefundAsync(RefundRequest(refundId, 4m), TestContext.Current.CancellationToken);
        var failed = await Provider(stripe).RefundAsync(RefundRequest(refundId, 4m), TestContext.Current.CancellationToken);

        Assert.Equal(new ProviderRefundResult(PaymentTrigger.Succeeded, "re_1"), succeeded);
        Assert.Equal(new ProviderRefundResult(PaymentTrigger.Pending, "re_2"), pending);
        Assert.Equal(new ProviderRefundResult(PaymentTrigger.Failed, "re_3", "expired_or_canceled_card"), failed);
        var sent = stripe.Requests[0];
        Assert.Equal((HttpMethod.Post, "/v1/refunds", refundId.ToString()), (sent.Method, sent.PathAndQuery, sent.IdempotencyKey));
        Assert.Superset(
            new HashSet<string> { "payment_intent=pi_1", "amount=400", $"metadata[tenant_id]={Tenant}", $"metadata[refund_id]={refundId}" },
            sent.Body.Split('&').Select(Uri.UnescapeDataString).ToHashSet());
    }

    [Fact]
    public async Task A_refused_refund_is_failed_an_ambiguous_one_is_unknown_and_its_status_is_found_by_id_or_metadata()
    {
        var refundId = Guid.CreateVersion7();
        var refused = await Provider(new FakeStripe().Answer(HttpStatusCode.BadRequest, """{ "error": { "code": "charge_disputed", "type": "invalid_request_error" } }"""))
            .RefundAsync(RefundRequest(refundId, 4m), TestContext.Current.CancellationToken);
        // Review fix R7: a refusal that may mean "already refunded" is an unknown outcome, settled by reconciliation.
        await Assert.ThrowsAsync<PaymentProviderUnavailableException>(() => Provider(new FakeStripe().Answer(HttpStatusCode.BadRequest, """{ "error": { "code": "charge_already_refunded" } }"""))
            .RefundAsync(RefundRequest(refundId, 4m), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<PaymentProviderUnavailableException>(() => Provider(new FakeStripe().Answer(HttpStatusCode.InternalServerError, "{}"))
            .RefundAsync(RefundRequest(refundId, 4m), TestContext.Current.CancellationToken));
        var stripe = new FakeStripe()
            .Answer(HttpStatusCode.OK, StripeFixtures.Refund("re_9", "succeeded", refundId))
            // Review fix R12: the second page is read when the first does not hold the refund.
            .Answer(HttpStatusCode.OK, $$"""{ "object": "list", "data": [{{StripeFixtures.Refund("re_8", "succeeded", Guid.CreateVersion7())}}], "has_more": true }""")
            .Answer(HttpStatusCode.OK, $$"""{ "object": "list", "data": [{{StripeFixtures.Refund("re_7", "pending", refundId)}}], "has_more": false }""");

        var byId = await Provider(stripe).GetRefundStatusAsync(new ProviderRefundQuery(refundId, Tenant, "pi_1", "re_9", "pm_card_visa"), TestContext.Current.CancellationToken);
        var byMetadata = await Provider(stripe).GetRefundStatusAsync(new ProviderRefundQuery(refundId, Tenant, "pi_1", null, "pm_card_visa"), TestContext.Current.CancellationToken);

        Assert.Equal(new ProviderRefundResult(PaymentTrigger.Failed, null, "charge_disputed"), refused);
        Assert.Equal(new ProviderRefundResult(PaymentTrigger.Succeeded, "re_9"), byId);
        Assert.Equal(new ProviderRefundResult(PaymentTrigger.Pending, "re_7"), byMetadata);
        Assert.Equal(["/v1/refunds/re_9", "/v1/refunds?payment_intent=pi_1&limit=100", "/v1/refunds?payment_intent=pi_1&limit=100&starting_after=re_8"], stripe.Requests.Select(static r => r.PathAndQuery));
    }

    private static ProviderRefundRequest RefundRequest(Guid refundId, decimal amount) => new(refundId, Tenant, PaymentId, "pi_1", "pm_card_visa", amount, "EUR");

    private static StripePaymentProvider Provider(FakeStripe stripe) =>
        new(new StripeTransport(new HttpClient(stripe, disposeHandler: false) { BaseAddress = new Uri("https://stripe.test/") }), Options.Create(new StripeOptions { SecretKey = TestKey }));

    private static ProviderPaymentRequest Request(decimal amount, string currency) => new(PaymentId, Tenant, amount, currency, "pm_card_visa");
}
