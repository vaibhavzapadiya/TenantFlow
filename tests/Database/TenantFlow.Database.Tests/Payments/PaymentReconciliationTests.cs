using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using TenantFlow.Payments.Application.Abstractions;

namespace TenantFlow.Database.Tests.Payments;

/// <summary>
/// SPEC-0006 criterion 12 against real PostgreSQL: payments whose outcome is unknown are settled by the Worker's
/// reconciliation timer, never charged again, escalated to review after five checks without a final answer, and still
/// settled by a late answer. The API runs on the real clock; the Worker harness runs timers at "now plus" a deadline.
/// </summary>
public sealed class PaymentReconciliationTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
{
    private int _charges;
    private WebApplicationFactory<Program>? _factory;
    private WorkerHarness? _worker;

    private WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("Not initialized.");

    private WorkerHarness Worker => _worker ?? throw new InvalidOperationException("Not initialized.");

    public ValueTask InitializeAsync()
    {
        _factory = ApiFactory.CreateProduction(connectionString: fixture.Database.AppConnectionString, settings: SimulatorWebhooks.Settings);
        _worker = WorkerHarness.Create(fixture.Database, inner => new ChargeCounter(inner, () => Interlocked.Increment(ref _charges)));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_worker is not null)
        {
            await _worker.DisposeAsync();
        }

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_timed_out_charge_is_settled_by_reconciliation_once_its_first_check_is_due_and_announced_once()
    {
        var s = await PaymentScenario.CreateAsync(fixture, Factory);
        var since = DateTimeOffset.UtcNow;
        var succeeded = await s.PayAsync(s.TenantA, await s.ReservedOrderAsync(s.TenantA), "sim_timeout_then_success");
        var declined = await s.PayAsync(s.TenantA, await s.ReservedOrderAsync(s.TenantA), "sim_timeout_then_decline");

        await Worker.RunTimersAfterAsync(TimeSpan.FromMinutes(1), since);
        var beforeDue = await StatusAsync(s, succeeded);
        await Worker.RunTimersAfterAsync(TimeSpan.FromMinutes(3), since);
        await Worker.RunTimersAfterAsync(TimeSpan.FromMinutes(30), since);

        Assert.Equal(("unknown", "succeeded", "failed"), (beforeDue, await StatusAsync(s, succeeded), await StatusAsync(s, declined)));
        Assert.Equal(1L, await s.CountAsync(s.TenantA, "SELECT count(*) FROM payments.outbox_messages WHERE payload->>'paymentId' = @id::text", succeeded));
        Assert.Equal("card_declined", await s.TextAsync(s.TenantA, "SELECT failure_code FROM payments.payments WHERE id = @id", declined));
        Assert.NotNull(await s.ScalarAsync(s.TenantA, "SELECT completed_at FROM payments.workflow_timers WHERE subject_id = @id", succeeded));
        Assert.Equal(0, _charges);
    }

    [Fact]
    public async Task Without_a_final_answer_a_payment_needs_review_after_five_checks_and_a_late_answer_still_settles_it()
    {
        var s = await PaymentScenario.CreateAsync(fixture, Factory);
        var since = DateTimeOffset.UtcNow;
        var order = await s.ReservedOrderAsync(s.TenantA);
        var payment = await s.PayAsync(s.TenantA, order, "sim_timeout_unknown");
        var neverSeen = await s.PayAsync(s.TenantA, await s.ReservedOrderAsync(s.TenantA), "sim_server_error");

        // Checks at +2, +3, +4, +5 minutes find no answer; the fifth, at +6, gives up.
        var statuses = new List<string?>();
        foreach (var minute in new[] { 2, 3, 4, 5, 6 })
        {
            await Worker.RunTimersAfterAsync(TimeSpan.FromMinutes(minute) + TimeSpan.FromSeconds(30), since);
            statuses.Add(await StatusAsync(s, payment));
        }

        using var read = await s.SendAsync(HttpMethod.Get, $"/v1/tenants/{s.TenantA}/orders/{order}/payments/current", s.CustomerA);
        var reviewEvents = await s.CountAsync(s.TenantA, "SELECT count(*) FROM payments.outbox_messages WHERE type = 'payment.review_required.v1' AND payload->>'paymentId' = @id::text", payment);
        using var late = await s.DeliverSignedAsync(SimulatorWebhooks.Event($"evt_{Guid.NewGuid():N}", "payment_intent.succeeded", s.TenantA, payment));

        Assert.Equal(["unknown", "unknown", "unknown", "unknown", "requires_review"], statuses);
        Assert.Equal("requires_review", (await PaymentScenario.JsonAsync(read)).GetProperty("status").GetString());
        Assert.Equal(1L, reviewEvents);
        Assert.Equal((HttpStatusCode.OK, "succeeded"), (late.StatusCode, await StatusAsync(s, payment)));
        // A payment the provider never saw is counted as unknown, never sent again.
        Assert.Equal("requires_review", await StatusAsync(s, neverSeen));
        Assert.Equal(0, _charges);
    }

    [Fact]
    public async Task A_request_interrupted_after_claiming_its_key_is_answered_once_reconciliation_settles_the_payment()
    {
        var s = await PaymentScenario.CreateAsync(fixture, Factory);
        var since = DateTimeOffset.UtcNow;
        var order = await s.ReservedOrderAsync(s.TenantA);
        var key = $"key-{Guid.NewGuid():N}";
        using var first = await PayWithKeyAsync(s, order, key);
        var payment = (await PaymentScenario.JsonAsync(first)).GetProperty("id").GetGuid();
        // As a crash right after the provider call would leave it: the key claimed, no answer stored.
        await s.ScalarAsync(s.TenantA, "UPDATE payments.idempotency_keys SET response_body = NULL, completed_at = NULL WHERE payment_id = @id", payment);
        using var whileOpen = await PayWithKeyAsync(s, order, key);

        await Worker.RunTimersAfterAsync(TimeSpan.FromMinutes(3), since);
        using var afterSettled = await PayWithKeyAsync(s, order, key);

        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.Created), (whileOpen.StatusCode, afterSettled.StatusCode));
        Assert.Equal("succeeded", (await PaymentScenario.JsonAsync(afterSettled)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_timer_runs_in_its_own_tenant_only()
    {
        var s = await PaymentScenario.CreateAsync(fixture, Factory);
        var since = DateTimeOffset.UtcNow;
        var aPayment = await s.PayAsync(s.TenantA, await s.ReservedOrderAsync(s.TenantA), "sim_timeout_then_success");
        // A timer that names tenant B but tenant A's payment: it must find nothing to settle.
        await s.ScalarAsync(s.TenantA, "UPDATE payments.workflow_timers SET tenant_id = '" + s.TenantB + "' WHERE subject_id = @id", aPayment);

        await Worker.RunTimersAfterAsync(TimeSpan.FromMinutes(3), since);

        Assert.Equal("unknown", await StatusAsync(s, aPayment));
        Assert.NotNull(await s.ScalarAsync(s.TenantA, "SELECT completed_at FROM payments.workflow_timers WHERE subject_id = @id", aPayment));
        Assert.Equal(0L, await s.CountAsync(s.TenantB, "SELECT count(*) FROM payments.outbox_messages WHERE payload->>'paymentId' = @id::text", aPayment));
    }

    // M6 review fix 1: a Worker configured for another provider still asks the one that took the payment.
    [Fact]
    public async Task A_payment_is_reconciled_by_the_provider_that_took_it_not_the_one_configured_now()
    {
        var s = await PaymentScenario.CreateAsync(fixture, Factory);
        var since = DateTimeOffset.UtcNow;
        var payment = await s.PayAsync(s.TenantA, await s.ReservedOrderAsync(s.TenantA), "sim_timeout_then_success");
        await using var stripeWorker = WorkerHarness.Create(fixture.Database, settings: new Dictionary<string, string?>
        {
            ["Payments:Provider"] = "stripe",
            ["Payments:Stripe:SecretKey"] = "sk_test_placeholder",
        });

        await stripeWorker.RunTimersAfterAsync(TimeSpan.FromMinutes(3), since);

        Assert.Equal("succeeded", await StatusAsync(s, payment));
    }

    private static Task<string?> StatusAsync(PaymentScenario s, Guid payment) =>
        s.TextAsync(s.TenantA, "SELECT status FROM payments.payments WHERE id = @id", payment);

    private async Task<HttpResponseMessage> PayWithKeyAsync(PaymentScenario s, Guid order, string key)
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/tenants/{s.TenantA}/orders/{order}/payments") { Content = JsonContent.Create(new { paymentMethod = "sim_timeout_then_success" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Shared.CreateToken(s.CustomerA));
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    // Reconciliation must never charge: any create call from the Worker is counted.
    private sealed class ChargeCounter(IPaymentProvider inner, Action charged) : IPaymentProvider
    {
        public string Name => inner.Name;

        public Task<ProviderPaymentResult> CreateAsync(ProviderPaymentRequest request, CancellationToken cancellationToken)
        {
            charged();
            return inner.CreateAsync(request, cancellationToken);
        }

        public Task<ProviderPaymentResult?> GetStatusAsync(ProviderStatusQuery query, CancellationToken cancellationToken) =>
            inner.GetStatusAsync(query, cancellationToken);

        public Task<ProviderRefundResult> RefundAsync(ProviderRefundRequest request, CancellationToken cancellationToken) =>
            inner.RefundAsync(request, cancellationToken);

        public Task<ProviderRefundResult?> GetRefundStatusAsync(ProviderRefundQuery query, CancellationToken cancellationToken) =>
            inner.GetRefundStatusAsync(query, cancellationToken);
    }
}
