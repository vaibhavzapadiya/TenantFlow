using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TenantFlow.Payments.Application.Abstractions;

namespace TenantFlow.Database.Tests.Payments;

/// <summary>
/// SPEC-0007 criteria 9 to 12 and 15 against real PostgreSQL, through the real host with the deterministic simulator.
/// Refund calls are counted per refund id, so "never refunded twice" is observed, not assumed. Orders cost 2 EUR a unit.
/// </summary>
public sealed class RefundEndpointTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
{
    private readonly ConcurrentDictionary<Guid, int> _refundCalls = new();
    private WebApplicationFactory<Program>? _factory;
    private PaymentScenario? _scenario;

    private PaymentScenario S => _scenario ?? throw new InvalidOperationException("Not initialized.");

    private WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("Not initialized.");

    public async ValueTask InitializeAsync()
    {
        _factory = CreateFactory(inner => new RefundCounter(inner, _refundCalls, gate: null));
        _scenario = await PaymentScenario.CreateAsync(fixture, _factory);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    // ---- criterion 9 ----

    [Fact]
    public async Task An_order_manager_refunds_part_of_a_paid_order_with_an_audited_reason_and_a_customer_cannot()
    {
        var (order, payment) = await PaidOrderAsync(units: 5, "sim_success");

        using var refunded = await RefundAsync(S.AdminA, order, 4m, "Damaged item", Key());
        using var byCustomer = await RefundAsync(S.CustomerA, order, 1m, "Mine", Key());
        using var noReason = await RefundAsync(S.AdminA, order, 1m, " ", Key());
        using var listed = await S.SendAsync(HttpMethod.Get, $"/v1/tenants/{S.TenantA}/orders/{order}/payments/current/refunds", S.AdminA);

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Forbidden, HttpStatusCode.BadRequest), (refunded.StatusCode, byCustomer.StatusCode, noReason.StatusCode));
        var refund = await PaymentScenario.JsonAsync(refunded);
        var refundId = refund.GetProperty("id").GetGuid();
        Assert.Equal(("succeeded", "refund", 4m, "EUR", "Damaged item"), (refund.GetProperty("status").GetString(), refund.GetProperty("kind").GetString(),
            refund.GetProperty("amount").GetProperty("amount").GetDecimal(), refund.GetProperty("amount").GetProperty("currency").GetString(), refund.GetProperty("reason").GetString()));
        Assert.Equal(refundId, Assert.Single((await PaymentScenario.JsonAsync(listed)).GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        // Audit: who asked is the administrator's membership; the payment counts what was given back.
        Assert.Equal(1, await S.CountAsync(S.TenantA, "SELECT count(*) FROM payments.refunds r JOIN payments.payments p ON p.id = r.payment_id WHERE r.id = @id AND r.requested_by_membership_id <> p.owner_membership_id AND r.reason = 'Damaged item'", refundId));
        Assert.Equal(4m, (decimal)(await S.ScalarAsync(S.TenantA, "SELECT refunded_amount FROM payments.payments WHERE id = @id", payment))!);
        // SPEC-0009 criterion 2: the refund request is audited once, naming the administrator by membership id only.
        Assert.Equal(1, await S.CountAsync(S.TenantA, AuditOutbox.Count("payments", "refund.requested", "refund", "SELECT requested_by_membership_id FROM payments.refunds WHERE id = @id"), refundId));
        Assert.Equal(1, _refundCalls.GetValueOrDefault(refundId));
        var events = await RefundEventsAsync(payment);
        using var succeeded = JsonDocument.Parse(Assert.Single(events, static e => e.Type == "payment.refund_succeeded.v1").Payload);
        Assert.Equal((4m, false, false), (succeeded.RootElement.GetProperty("refundedTotal").GetDecimal(), succeeded.RootElement.GetProperty("fullyRefunded").GetBoolean(),
            succeeded.RootElement.GetProperty("cancellation").GetBoolean()));
    }

    // ---- criterion 10 ----

    [Fact]
    public async Task Refunds_never_add_up_to_more_than_was_captured_even_when_sent_at_once()
    {
        var (order, payment) = await PaidOrderAsync(units: 5, "sim_success");
        var (unpaid, _) = await PaidOrderAsync(units: 1, "sim_decline");

        using var first = await RefundAsync(S.AdminA, order, 4m, "Partial", Key());
        using var tooMuch = await RefundAsync(S.AdminA, order, 6.01m, "Too much", Key());
        var racing = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => RefundAsync(S.AdminA, order, 2m, "Racing", Key())));
        using var failedPayment = await RefundAsync(S.AdminA, unpaid, 1m, "Never paid", Key());

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Conflict), (first.StatusCode, tooMuch.StatusCode));
        Assert.Equal("payments.refund_exceeds_captured", await PaymentScenario.CodeAsync(tooMuch));
        Assert.Equal(3, racing.Count(static r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(3, racing.Count(static r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal((HttpStatusCode.Conflict, "payments.not_refundable"), (failedPayment.StatusCode, await PaymentScenario.CodeAsync(failedPayment)));
        Assert.Equal(10m, (decimal)(await S.ScalarAsync(S.TenantA, "SELECT sum(amount) FROM payments.refunds WHERE payment_id = @id", payment))!);
        Assert.Equal(10m, (decimal)(await S.ScalarAsync(S.TenantA, "SELECT refunded_amount FROM payments.payments WHERE id = @id", payment))!);
        using var last = JsonDocument.Parse((await RefundEventsAsync(payment)).Last(static e => e.Type == "payment.refund_succeeded.v1").Payload);
        Assert.Equal((10m, true), (last.RootElement.GetProperty("refundedTotal").GetDecimal(), last.RootElement.GetProperty("fullyRefunded").GetBoolean()));
        foreach (var response in racing)
        {
            response.Dispose();
        }
    }

    // ---- criterion 11 ----

    [Fact]
    public async Task A_repeated_refund_key_replays_another_body_is_refused_and_an_unfinished_one_is_in_progress()
    {
        var (order, payment) = await PaidOrderAsync(units: 5, "sim_success");
        var key = Key();

        using var first = await RefundAsync(S.AdminA, order, 3m, "Late delivery", key);
        using var replay = await RefundAsync(S.AdminA, order, 3m, "Late delivery", key);
        using var otherBody = await RefundAsync(S.AdminA, order, 2m, "Late delivery", key);
        var gate = new Gate();
        await using var blocking = CreateFactory(inner => new RefundCounter(inner, _refundCalls, gate));
        var gatedKey = Key();
        var gatedTask = RefundAsync(blocking, S.AdminA, order, 1m, "Gated", gatedKey);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        using var whileRunning = await RefundAsync(blocking, S.AdminA, order, 1m, "Gated", gatedKey);
        gate.Release.SetResult();
        using var gated = await gatedTask;

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created), (first.StatusCode, replay.StatusCode));
        var refundId = (await PaymentScenario.JsonAsync(first)).GetProperty("id").GetGuid();
        Assert.Equal(refundId, (await PaymentScenario.JsonAsync(replay)).GetProperty("id").GetGuid());
        Assert.Equal(1, _refundCalls.GetValueOrDefault(refundId));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "payments.idempotency_key_reused"), (otherBody.StatusCode, await PaymentScenario.CodeAsync(otherBody)));
        Assert.Equal((HttpStatusCode.Conflict, "payments.request_in_progress"), (whileRunning.StatusCode, await PaymentScenario.CodeAsync(whileRunning)));
        Assert.Equal(HttpStatusCode.Created, gated.StatusCode);
        Assert.Equal(2, await S.CountAsync(S.TenantA, "SELECT count(*) FROM payments.refunds WHERE payment_id = @id", payment));
    }

    // ---- criterion 12 ----

    [Fact]
    public async Task A_refund_with_an_unknown_outcome_is_reconciled_and_a_signed_refund_webhook_settles_another_once()
    {
        var (order, payment) = await PaidOrderAsync(units: 5, "sim_refund_timeout");
        using var reconciled = await RefundAsync(S.AdminA, order, 2m, "Reconciled", Key());
        using var hooked = await RefundAsync(S.AdminA, order, 3m, "Webhook", Key());
        var (reconciledBody, hookedBody) = (await PaymentScenario.JsonAsync(reconciled), await PaymentScenario.JsonAsync(hooked));
        var (reconciledId, hookedId) = (reconciledBody.GetProperty("id").GetGuid(), hookedBody.GetProperty("id").GetGuid());
        var created = DateTimeOffset.UtcNow;

        // The webhook settles one; a second delivery of the same event changes nothing; a forged reference acts on nothing.
        var eventId = $"evt_{Guid.NewGuid():N}";
        using var delivered = await S.DeliverSignedAsync(SimulatorWebhooks.RefundEvent(eventId, "refund.updated", S.TenantA, hookedId, "succeeded"));
        using var duplicate = await S.DeliverSignedAsync(SimulatorWebhooks.RefundEvent(eventId, "refund.updated", S.TenantA, hookedId, "succeeded"));
        using var forged = await S.DeliverSignedAsync(SimulatorWebhooks.RefundEvent($"evt_{Guid.NewGuid():N}", "refund.failed", S.TenantB, hookedId, "failed"));
        var afterWebhook = await S.TextAsync(S.TenantA, "SELECT status FROM payments.refunds WHERE id = @id", reconciledId);
        // Reconciliation asks the provider about the other after two minutes.
        await using var worker = WorkerHarness.Create(fixture.Database);
        await worker.RunTimersAfterAsync(TimeSpan.FromMinutes(3), created);

        Assert.Equal(("processing", "processing"), (reconciledBody.GetProperty("status").GetString(), hookedBody.GetProperty("status").GetString()));
        Assert.Equal("unknown", afterWebhook);
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.NotFound), (delivered.StatusCode, duplicate.StatusCode, forged.StatusCode));
        Assert.Equal(("succeeded", "succeeded"), (await S.TextAsync(S.TenantA, "SELECT status FROM payments.refunds WHERE id = @id", hookedId),
            await S.TextAsync(S.TenantA, "SELECT status FROM payments.refunds WHERE id = @id", reconciledId)));
        Assert.Equal(5m, (decimal)(await S.ScalarAsync(S.TenantA, "SELECT refunded_amount FROM payments.payments WHERE id = @id", payment))!);
        Assert.Equal(2, (await RefundEventsAsync(payment)).Count(static e => e.Type == "payment.refund_succeeded.v1"));
        Assert.Equal((1, 1), (_refundCalls.GetValueOrDefault(reconciledId), _refundCalls.GetValueOrDefault(hookedId)));
    }

    // SPEC-0010 criterion 4 and 11 (PY-A25): a refund the provider fails after reporting success stays succeeded, is logged,
    // and is counted as reversed, so an alert can page on it.
    [Fact]
    public async Task A_refund_failed_after_it_succeeded_is_counted_as_reversed()
    {
        var (order, _) = await PaidOrderAsync(units: 2, "sim_refund_timeout");
        using var refunds = new MeterCapture("tenantflow.refunds.completed");
        using var refund = await RefundAsync(S.AdminA, order, 2m, "Reversed later", Key());
        var refundId = (await PaymentScenario.JsonAsync(refund)).GetProperty("id").GetGuid();

        using var succeeded = await S.DeliverSignedAsync(SimulatorWebhooks.RefundEvent($"evt_{Guid.NewGuid():N}", "refund.updated", S.TenantA, refundId, "succeeded"));
        using var reversed = await S.DeliverSignedAsync(SimulatorWebhooks.RefundEvent($"evt_{Guid.NewGuid():N}", "refund.failed", S.TenantA, refundId, "failed"));

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (succeeded.StatusCode, reversed.StatusCode));
        Assert.Equal("succeeded", await S.TextAsync(S.TenantA, "SELECT status FROM payments.refunds WHERE id = @id", refundId));
        Assert.Equal(["unknown", "succeeded", "reversed"], refunds.Measurements.Select(static m => m.Tags["outcome"]));
    }

    // ---- criterion 15 ----

    [Fact]
    public async Task Another_tenant_can_neither_refund_nor_list_refunds_of_this_tenants_order()
    {
        var (order, payment) = await PaidOrderAsync(units: 1, "sim_success");

        using var refund = await RefundAsync(S.AdminB, order, 1m, "Not mine", Key(), S.TenantB);
        using var list = await S.SendAsync(HttpMethod.Get, $"/v1/tenants/{S.TenantB}/orders/{order}/payments/current/refunds", S.AdminB);

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (refund.StatusCode, list.StatusCode));
        Assert.Equal(("payments.payment_not_found", "payments.payment_not_found"), (await PaymentScenario.CodeAsync(refund), await PaymentScenario.CodeAsync(list)));
        Assert.Equal(0, await S.CountAsync(S.TenantA, "SELECT count(*) FROM payments.refunds WHERE payment_id = @id", payment));
        Assert.Equal(0, await S.CountAsync(S.TenantB, "SELECT count(*) FROM payments.refunds WHERE payment_id = @id", payment));
    }

    // ---- helpers ----

    private static string Key() => $"refund-{Guid.NewGuid():N}";

    private WebApplicationFactory<Program> CreateFactory(Func<IPaymentProvider, IPaymentProvider> wrap) =>
        ApiFactory.CreateProduction(
            services =>
            {
                var original = services.Single(static d => d.ServiceType == typeof(PaymentProviders));
                services.Remove(original);
                services.AddSingleton(provider =>
                {
                    var adapters = (PaymentProviders)original.ImplementationFactory!(provider);
                    return new PaymentProviders(adapters.All.Select(wrap), adapters.Current.Name);
                });
            },
            connectionString: fixture.Database.AppConnectionString,
            settings: SimulatorWebhooks.Settings);

    private async Task<(Guid Order, Guid Payment)> PaidOrderAsync(int units, string paymentMethod)
    {
        var order = await S.ReservedOrderAsync(S.TenantA, onHand: units, quantity: units);
        return (order, await S.PayAsync(S.TenantA, order, paymentMethod));
    }

    private Task<HttpResponseMessage> RefundAsync(string subject, Guid order, decimal amount, string reason, string key, Guid? tenant = null) =>
        RefundAsync(Factory, subject, order, amount, reason, key, tenant);

    private async Task<HttpResponseMessage> RefundAsync(WebApplicationFactory<Program> factory, string subject, Guid order, decimal amount, string reason, string key, Guid? tenant = null)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/tenants/{tenant ?? S.TenantA}/orders/{order}/payments/current/refunds")
        {
            Content = JsonContent.Create(new { amount, reason }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Shared.CreateToken(subject));
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed record EventRow(string Type, string Payload);

    private async Task<List<EventRow>> RefundEventsAsync(Guid payment)
    {
        var rows = new List<EventRow>();
        var count = await S.CountAsync(S.TenantA, "SELECT count(*) FROM payments.outbox_messages WHERE type LIKE 'payment.refund_%' AND payload->>'paymentId' = @id::text", payment);
        for (var i = 0; i < count; i++)
        {
            var type = await S.TextAsync(S.TenantA, $"SELECT type FROM payments.outbox_messages WHERE type LIKE 'payment.refund_%' AND payload->>'paymentId' = @id::text ORDER BY occurred_at, id OFFSET {i} LIMIT 1", payment);
            var payload = await S.TextAsync(S.TenantA, $"SELECT payload::text FROM payments.outbox_messages WHERE type LIKE 'payment.refund_%' AND payload->>'paymentId' = @id::text ORDER BY occurred_at, id OFFSET {i} LIMIT 1", payment);
            rows.Add(new EventRow(type!, payload!));
        }

        return rows;
    }

    private sealed class Gate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RefundCounter(IPaymentProvider inner, ConcurrentDictionary<Guid, int> calls, Gate? gate) : IPaymentProvider
    {
        public string Name => inner.Name;

        public Task<ProviderPaymentResult> CreateAsync(ProviderPaymentRequest request, CancellationToken cancellationToken) => inner.CreateAsync(request, cancellationToken);

        public Task<ProviderPaymentResult?> GetStatusAsync(ProviderStatusQuery query, CancellationToken cancellationToken) => inner.GetStatusAsync(query, cancellationToken);

        public async Task<ProviderRefundResult> RefundAsync(ProviderRefundRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            calls.AddOrUpdate(request.RefundId, 1, static (_, n) => n + 1);
            if (gate is not null)
            {
                gate.Entered.TrySetResult();
                await gate.Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }

            return await inner.RefundAsync(request, cancellationToken);
        }

        public Task<ProviderRefundResult?> GetRefundStatusAsync(ProviderRefundQuery query, CancellationToken cancellationToken) => inner.GetRefundStatusAsync(query, cancellationToken);
    }
}
