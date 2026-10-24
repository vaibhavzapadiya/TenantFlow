using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TenantFlow.Database.Tests.Payments;

/// <summary>
/// SPEC-0006 criteria 7 to 10 against real PostgreSQL, through the real host: signed simulator webhooks, delivered forged,
/// stale, duplicated and out of order (criterion 14's test helpers). Tenants A and B each have a payment-ready order.
/// </summary>
public sealed class PaymentWebhookTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
{
    private const string Succeeded = "payment_intent.succeeded";
    private const string Failed = "payment_intent.payment_failed";
    private const string Processing = "payment_intent.processing";

    private WebApplicationFactory<Program>? _factory;

    private WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("Not initialized.");

    public ValueTask InitializeAsync()
    {
        _factory = ApiFactory.CreateProduction(connectionString: fixture.Database.AppConnectionString, settings: SimulatorWebhooks.Settings);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    // ---- criterion 7 ----

    [Fact]
    public async Task A_signed_webhook_settles_an_unknown_payment_and_announces_it_once()
    {
        var s = await PaymentScenario.CreateAsync(fixture, Factory);
        var order = await s.ReservedOrderAsync(s.TenantA);
        var payment = await s.PayAsync(s.TenantA, order, "sim_timeout_then_success");

        using var response = await s.DeliverSignedAsync(SimulatorWebhooks.Event(EventId(), Succeeded, s.TenantA, payment));
        using var read = await s.SendAsync(HttpMethod.Get, $"/v1/tenants/{s.TenantA}/orders/{order}/payments/current", s.CustomerA);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("succeeded", (await PaymentScenario.JsonAsync(read)).GetProperty("status").GetString());
        Assert.Equal(SimulatorWebhooks.ProviderPaymentId(payment), await s.TextAsync(s.TenantA, "SELECT provider_payment_id FROM payments.payments WHERE id = @id", payment));
        Assert.Equal(1L, await s.CountAsync(s.TenantA, "SELECT count(*) FROM payments.outbox_messages WHERE type = 'payment.succeeded.v1' AND payload->>'paymentId' = @id::text", payment));
        Assert.Equal(1L, await s.CountAsync(s.TenantA, "SELECT count(*) FROM payments.webhook_events WHERE payment_id = @id AND outcome = 'applied'", payment));
    }

    // ---- criterion 8 ----

    [Fact]
    public async Task Forged_stale_tampered_or_malformed_webhooks_are_refused_and_change_nothing()
    {
        var s = await PaymentScenario.CreateAsync(fixture, Factory);
        var payment = await s.PayAsync(s.TenantA, await s.ReservedOrderAsync(s.TenantA), "sim_timeout_then_success");
        var body = SimulatorWebhooks.Event(EventId(), Succeeded, s.TenantA, payment);
        var now = DateTimeOffset.UtcNow;

        using var unsigned = await s.DeliverAsync(body, signature: null);
        using var wrongSecret = await s.DeliverAsync(body, SimulatorWebhooks.Sign(body, now, "not-the-secret"));
        using var tampered = await s.DeliverAsync(body.Replace(Succeeded, Failed, StringComparison.Ordinal), SimulatorWebhooks.Sign(body, now));
        using var stale = await s.DeliverAsync(body, SimulatorWebhooks.Sign(body, now.AddMinutes(-6)));
        using var future = await s.DeliverAsync(body, SimulatorWebhooks.Sign(body, now.AddMinutes(6)));
        using var malformed = await s.DeliverAsync("not json", SimulatorWebhooks.Sign("not json", now));
        using var unknownProvider = await s.DeliverAsync(body, SimulatorWebhooks.Sign(body, now), "/v1/payment-webhooks/acme");

        Assert.Equal(
            [HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.BadRequest, HttpStatusCode.NotFound],
            [unsigned.StatusCode, wrongSecret.StatusCode, tampered.StatusCode, stale.StatusCode, future.StatusCode, malformed.StatusCode, unknownProvider.StatusCode]);
        Assert.Equal(
            ["payments.webhook.signature_invalid", "payments.webhook.signature_invalid", "payments.webhook.signature_invalid", "payments.webhook.timestamp_out_of_tolerance", "payments.webhook.timestamp_out_of_tolerance", "payments.webhook.malformed", "payments.webhook.provider_unknown"],
            [await PaymentScenario.CodeAsync(unsigned), await PaymentScenario.CodeAsync(wrongSecret), await PaymentScenario.CodeAsync(tampered), await PaymentScenario.CodeAsync(stale),
             await PaymentScenario.CodeAsync(future), await PaymentScenario.CodeAsync(malformed), await PaymentScenario.CodeAsync(unknownProvider)]);
        Assert.Equal("unknown", await s.TextAsync(s.TenantA, "SELECT status FROM payments.payments WHERE id = @id", payment));
        Assert.Equal(0L, await s.CountAsync(s.TenantA, "SELECT count(*) FROM payments.webhook_events WHERE payment_id = @id", payment));
    }

    [Fact]
    public async Task Without_a_configured_secret_every_webhook_is_refused()
    {
        await using var unconfigured = ApiFactory.CreateProduction(connectionString: fixture.Database.AppConnectionString);
        var s = await PaymentScenario.CreateAsync(fixture, unconfigured);
        var payment = await s.PayAsync(s.TenantA, await s.ReservedOrderAsync(s.TenantA), "sim_timeout_then_success");

        using var response = await s.DeliverSignedAsync(SimulatorWebhooks.Event(EventId(), Succeeded, s.TenantA, payment));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unknown", await s.TextAsync(s.TenantA, "SELECT status FROM payments.payments WHERE id = @id", payment));
    }

    // M6 review fix 7: a correctly signed event created elsewhere on the account is acknowledged, so the provider stops retrying.
    [Fact]
    public async Task A_signed_event_without_tenantflow_metadata_is_acknowledged_and_changes_nothing()
    {
        var s = await PaymentScenario.CreateAsync(fixture, Factory);
        var body = """{"id":"evt_foreign","object":"event","type":"payment_intent.succeeded","data":{"object":{"id":"pi_foreign","object":"payment_intent","metadata":{}}}}""";

        using var response = await s.DeliverSignedAsync(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0L, await s.CountAsync(s.TenantA, "SELECT count(*) FROM payments.webhook_events WHERE @id IS NOT NULL AND event_id = 'evt_foreign'", s.TenantA));
    }

    // ---- criterion 9 ----

    [Fact]
    public async Task Duplicate_and_out_of_order_webhooks_change_nothing_and_announce_nothing_twice()
    {
        var s = await PaymentScenario.CreateAsync(fixture, Factory);
        var payment = await s.PayAsync(s.TenantA, await s.ReservedOrderAsync(s.TenantA), "sim_timeout_then_success");
        var success = SimulatorWebhooks.Event(EventId(), Succeeded, s.TenantA, payment);

        // The provider's success, the same delivery again, then a late "processing" and a contradicting failure.
        using var first = await s.DeliverSignedAsync(success);
        using var duplicate = await s.DeliverSignedAsync(success);
        using var late = await s.DeliverSignedAsync(SimulatorWebhooks.Event(EventId(), Processing, s.TenantA, payment));
        using var contradicting = await s.DeliverSignedAsync(SimulatorWebhooks.Event(EventId(), Failed, s.TenantA, payment, failureCode: "card_declined"));

        Assert.All([first, duplicate, late, contradicting], static r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal("succeeded", await s.TextAsync(s.TenantA, "SELECT status FROM payments.payments WHERE id = @id", payment));
        Assert.Null(await s.TextAsync(s.TenantA, "SELECT failure_code FROM payments.payments WHERE id = @id", payment));
        Assert.Equal(1L, await s.CountAsync(s.TenantA, "SELECT count(*) FROM payments.outbox_messages WHERE payload->>'paymentId' = @id::text", payment));
        Assert.Equal("applied,ignored,ignored", await s.TextAsync(s.TenantA, "SELECT string_agg(outcome, ',' ORDER BY received_at, outcome) FROM payments.webhook_events WHERE payment_id = @id", payment));
    }

    // ---- criterion 10: a webhook never acts across tenants ----

    [Fact]
    public async Task A_correctly_signed_webhook_cannot_act_on_another_tenants_payment_or_a_mismatched_one()
    {
        var s = await PaymentScenario.CreateAsync(fixture, Factory);
        var aPayment = await s.PayAsync(s.TenantA, await s.ReservedOrderAsync(s.TenantA), "sim_timeout_then_success");
        var declined = await s.PayAsync(s.TenantA, await s.ReservedOrderAsync(s.TenantA), "sim_decline");

        // Tenant B's metadata naming tenant A's payment; an unknown tenant; A's declined payment under another provider id.
        using var crossTenant = await s.DeliverSignedAsync(SimulatorWebhooks.Event(EventId(), Succeeded, s.TenantB, aPayment));
        using var unknownTenant = await s.DeliverSignedAsync(SimulatorWebhooks.Event(EventId(), Succeeded, Guid.CreateVersion7(), aPayment));
        using var mismatched = await s.DeliverSignedAsync(SimulatorWebhooks.Event(EventId(), Succeeded, s.TenantA, declined, providerPaymentId: "sim_pi_someone_else"));

        Assert.Equal([HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound], [crossTenant.StatusCode, unknownTenant.StatusCode, mismatched.StatusCode]);
        Assert.Equal("payments.webhook.payment_not_found", await PaymentScenario.CodeAsync(unknownTenant));
        Assert.Equal(("unknown", "failed"), (await s.TextAsync(s.TenantA, "SELECT status FROM payments.payments WHERE id = @id", aPayment),
            await s.TextAsync(s.TenantA, "SELECT status FROM payments.payments WHERE id = @id", declined)));
        Assert.Equal(0L, await s.CountAsync(s.TenantB, "SELECT count(*) FROM payments.webhook_events WHERE payment_id = @id", aPayment));
        Assert.Equal(0L, await s.CountAsync(s.TenantA, "SELECT count(*) FROM payments.webhook_events WHERE payment_id = @id", aPayment));
        Assert.Equal(1L, await s.CountAsync(s.TenantA, "SELECT count(*) FROM payments.outbox_messages WHERE payload->>'paymentId' = @id::text", declined));
    }

    private static string EventId() => $"evt_{Guid.NewGuid():N}";
}
