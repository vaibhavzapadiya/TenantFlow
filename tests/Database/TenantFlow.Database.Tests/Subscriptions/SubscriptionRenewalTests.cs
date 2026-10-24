using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TenantFlow.BuildingBlocks.Abstractions.Workflow;
using TenantFlow.Payments.Application.Abstractions;
using TenantFlow.Payments.Contracts;
using TenantFlow.Payments.Domain.Payments;
using TenantFlow.Subscriptions.Application.Abstractions;
using TenantFlow.Subscriptions.Application.Subscriptions.Services;

namespace TenantFlow.Database.Tests.Subscriptions;

/// <summary>
/// SPEC-0007 criteria 5 to 7 against real PostgreSQL: subscriptions are made through the real API, and their due rows are
/// run by the Worker's modules (<see cref="WorkerHarness"/>) on a clock the test moves, as Quartz would run them. Every
/// provider call is counted per payment, so "never charged twice" is observed, not assumed.
/// </summary>
public sealed class SubscriptionRenewalTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
{
    private static readonly TimeSpan Trial = TimeSpan.FromDays(14);
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<Guid, int> _calls = new();
    private WebApplicationFactory<Program>? _factory;
    private WorkerHarness? _worker;
    private PaymentScenario? _scenario;

    private PaymentScenario S => _scenario ?? throw new InvalidOperationException("Not initialized.");

    private WorkerHarness Worker => _worker ?? throw new InvalidOperationException("Not initialized.");

    public async ValueTask InitializeAsync()
    {
        _factory = ApiFactory.CreateProduction(connectionString: fixture.Database.AppConnectionString);
        _worker = WorkerHarness.Create(fixture.Database, inner => new CountingProvider(inner, _calls));
        _scenario = await PaymentScenario.CreateAsync(fixture, _factory);
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

    // ---- criterion 5 ----

    [Fact]
    public async Task A_trial_is_charged_when_it_ends_and_each_period_after_counted_from_the_anchor()
    {
        var (subscription, created) = await SubscribeAsync(trialDays: 14, "sim_success");

        await Worker.RunTimersAfterAsync(Trial + Minute, created);
        var anchor = await TimeAsync(S.TenantA, "SELECT anchor_at FROM subscriptions.subscriptions WHERE id = @id", subscription);
        var firstEnd = await TimeAsync(S.TenantA, "SELECT period_end FROM subscriptions.subscriptions WHERE id = @id", subscription);
        await Worker.RunTimersAfterAsync(Trial + Minute + Minute, created);
        var afterRepeat = await CountAsync(S.TenantA, "SELECT count(*) FROM payments.payments WHERE subscription_id = @id", subscription);
        await Worker.RunTimersAfterAsync(firstEnd - created + Minute, created);

        Assert.Equal(anchor.AddMonths(1), firstEnd);
        Assert.Equal(1, afterRepeat);
        Assert.Equal(("active", 2L), (await S.TextAsync(S.TenantA, "SELECT status FROM subscriptions.subscriptions WHERE id = @id", subscription),
            await CountAsync(S.TenantA, "SELECT period FROM subscriptions.subscriptions WHERE id = @id", subscription)));
        Assert.Equal(anchor.AddMonths(2), await TimeAsync(S.TenantA, "SELECT period_end FROM subscriptions.subscriptions WHERE id = @id", subscription));
        // One succeeded payment per period, for the plan's price, in the subscription's tenant only.
        Assert.Equal(2, await CountAsync(S.TenantA, "SELECT count(*) FROM payments.payments WHERE subscription_id = @id AND status = 'succeeded' AND amount = 9.99 AND currency = 'EUR' AND charge_attempt = 1 AND order_id IS NULL", subscription));
        Assert.Equal(0, await CountAsync(S.TenantB, "SELECT count(*) FROM payments.payments WHERE subscription_id = @id", subscription));
        Assert.Equal(anchor.AddMonths(2), await TimerDueAsync(subscription));
        // Charges of subscriptions announce nothing to Orders.
        Assert.Equal(0, await CountAsync(S.TenantA, "SELECT count(*) FROM payments.outbox_messages WHERE payload::text LIKE '%' || @id::text || '%'", subscription));
    }

    // ---- criterion 6 ----

    [Fact]
    public async Task A_renewal_that_runs_again_after_charging_or_twice_at_once_charges_once_and_advances_once()
    {
        var (subscription, created) = await SubscribeAsync(trialDays: 0, "sim_success");
        Worker.Clock.SetUtcNow(created + Minute);

        // A run that charged and crashed before saving: the payment exists, the subscription is still incomplete.
        var charged = await Worker.InTenantAsync(S.TenantA, services => services.GetRequiredService<ISubscriptionCharges>().ChargeAsync(
            new SubscriptionCharge(subscription, 1, 1, Guid.CreateVersion7(), 9.99m, "EUR", "sim_success"), TestContext.Current.CancellationToken));
        // Then the row is run twice at once (a lost lease), and once more by the runner.
        await Task.WhenAll(RenewAsync(subscription), RenewAsync(subscription));
        await Worker.RunTimersAfterAsync(Minute, created);

        Assert.Equal(SubscriptionChargeStatus.Succeeded, charged.Status);
        Assert.Equal(1, await CountAsync(S.TenantA, "SELECT count(*) FROM payments.payments WHERE subscription_id = @id", subscription));
        Assert.Equal(1, _calls.GetValueOrDefault(charged.PaymentId));
        Assert.Equal(("active", 1L), (await S.TextAsync(S.TenantA, "SELECT status FROM subscriptions.subscriptions WHERE id = @id", subscription),
            await CountAsync(S.TenantA, "SELECT period FROM subscriptions.subscriptions WHERE id = @id", subscription)));
    }

    [Fact]
    public async Task One_attempt_holds_one_payment_and_a_stale_renewal_cannot_move_a_subscription_back()
    {
        var (subscription, created) = await SubscribeAsync(trialDays: 0, "sim_success");
        Worker.Clock.SetUtcNow(created + Minute);

        // Two payments for one attempt: the second is refused by the database, whoever sends it.
        var started = await Worker.InTenantAsync(S.TenantA, async services =>
        {
            var store = services.GetRequiredService<IPaymentStore>();
            var first = await store.TryStartChargeAsync(Charge(subscription, created), TestContext.Current.CancellationToken);
            var second = await store.TryStartChargeAsync(Charge(subscription, created), TestContext.Current.CancellationToken);
            return (first, second);
        });

        // A renewal that read the subscription before two others advanced it saves nothing.
        var (staleSaved, period) = await Worker.InTenantAsync(S.TenantA, async services =>
        {
            var store = services.GetRequiredService<ISubscriptionStore>();
            var stale = (await store.FindAsync(subscription, TestContext.Current.CancellationToken))!;
            for (var paid = 0; paid < 2; paid++)
            {
                var current = (await store.FindAsync(subscription, TestContext.Current.CancellationToken))!;
                var read = RenewalState.Of(current);
                current.ChargeSucceeded(created);
                Assert.True(await store.SaveRenewalAsync(current, read, TestContext.Current.CancellationToken));
            }

            var staleRead = RenewalState.Of(stale);
            stale.ChargeSucceeded(created);
            var saved = await store.SaveRenewalAsync(stale, staleRead, TestContext.Current.CancellationToken);
            return (saved, (await store.FindAsync(subscription, TestContext.Current.CancellationToken))!.Period);
        });

        Assert.Equal((true, false), started);
        Assert.Equal(1, await CountAsync(S.TenantA, "SELECT count(*) FROM payments.payments WHERE subscription_id = @id", subscription));
        Assert.Equal((false, 2), (staleSaved, period));
    }

    // ---- criterion 7 ----

    [Fact]
    public async Task A_declined_renewal_is_past_due_retried_daily_and_cancelled_after_the_third_attempt()
    {
        var (subscription, created) = await SubscribeAsync(trialDays: 14, "sim_decline");

        await Worker.RunTimersAfterAsync(Trial + Minute, created);
        var afterFirst = (await S.TextAsync(S.TenantA, "SELECT status FROM subscriptions.subscriptions WHERE id = @id", subscription), await TimerDueAsync(subscription));
        await Worker.RunTimersAfterAsync(Trial + Minute + TimeSpan.FromDays(1) + Minute, created);
        var afterSecond = await CountAsync(S.TenantA, "SELECT failed_attempts FROM subscriptions.subscriptions WHERE id = @id", subscription);
        await Worker.RunTimersAfterAsync(Trial + TimeSpan.FromDays(2) + TimeSpan.FromMinutes(5), created);

        Assert.Equal(("past_due", created + Trial + Minute + SubscriptionRenewals.RetryAfter), afterFirst);
        Assert.Equal(2, afterSecond);
        Assert.Equal(("cancelled", "payment_failed"), (await S.TextAsync(S.TenantA, "SELECT status FROM subscriptions.subscriptions WHERE id = @id", subscription),
            await S.TextAsync(S.TenantA, "SELECT cancel_reason FROM subscriptions.subscriptions WHERE id = @id", subscription)));
        Assert.Equal(3, await CountAsync(S.TenantA, "SELECT count(DISTINCT charge_attempt) FROM payments.payments WHERE subscription_id = @id AND status = 'failed' AND billing_period = 1", subscription));
        Assert.Equal(3, await CountAsync(S.TenantA, "SELECT count(*) FROM payments.payments WHERE subscription_id = @id", subscription));
        Assert.True(await TimerCompletedAsync(subscription));
    }

    [Fact]
    public async Task A_declined_first_charge_ends_the_subscription_and_a_cancelled_trial_is_never_charged()
    {
        var (declined, created) = await SubscribeAsync(trialDays: 0, "sim_decline");
        var (trial, _) = await SubscribeAsync(trialDays: 14, "sim_success");
        using var cancel = await S.SendAsync(HttpMethod.Post, $"/v1/tenants/{S.TenantA}/subscriptions/{trial}/cancel", S.CustomerA);

        await Worker.RunTimersAfterAsync(Trial + Minute, created);

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(("cancelled", "payment_failed"), (await S.TextAsync(S.TenantA, "SELECT status FROM subscriptions.subscriptions WHERE id = @id", declined),
            await S.TextAsync(S.TenantA, "SELECT cancel_reason FROM subscriptions.subscriptions WHERE id = @id", declined)));
        Assert.Equal(1, await CountAsync(S.TenantA, "SELECT count(*) FROM payments.payments WHERE subscription_id = @id", declined));
        Assert.Equal(("cancelled", "requested"), (await S.TextAsync(S.TenantA, "SELECT status FROM subscriptions.subscriptions WHERE id = @id", trial),
            await S.TextAsync(S.TenantA, "SELECT cancel_reason FROM subscriptions.subscriptions WHERE id = @id", trial)));
        Assert.Equal(0, await CountAsync(S.TenantA, "SELECT count(*) FROM payments.payments WHERE subscription_id = @id", trial));
    }

    [Fact]
    public async Task A_charge_with_an_unknown_outcome_is_asked_about_again_and_never_charged_again()
    {
        var (subscription, created) = await SubscribeAsync(trialDays: 0, "sim_timeout_unknown");

        await Worker.RunTimersAfterAsync(Minute, created);
        var due = await TimerDueAsync(subscription);
        await Worker.RunTimersAfterAsync(TimeSpan.FromMinutes(30), created);

        Assert.Equal(created + Minute + SubscriptionRenewals.PendingCheckAfter, due);
        Assert.Equal("incomplete", await S.TextAsync(S.TenantA, "SELECT status FROM subscriptions.subscriptions WHERE id = @id", subscription));
        Assert.Equal(1, await CountAsync(S.TenantA, "SELECT count(*) FROM payments.payments WHERE subscription_id = @id", subscription));
        var payment = Guid.Parse((await S.TextAsync(S.TenantA, "SELECT id::text FROM payments.payments WHERE subscription_id = @id", subscription))!);
        Assert.Equal(1, _calls.GetValueOrDefault(payment));
    }

    // ---- review fixes ----

    // R1: a charge still in flight when the subscription ended is given back once it succeeds, once.
    [Fact]
    public async Task A_charge_that_succeeds_after_its_subscription_ended_is_refunded_once()
    {
        var (subscription, created) = await SubscribeAsync(trialDays: 0, "sim_timeout_then_success");

        await Worker.RunTimersAfterAsync(Minute, created);
        using var cancelled = await S.SendAsync(HttpMethod.Post, $"/v1/tenants/{S.TenantA}/subscriptions/{subscription}/cancel", S.CustomerA);
        await Worker.RunTimersAfterAsync(TimeSpan.FromMinutes(10), created);
        await Worker.RunTimersAfterAsync(TimeSpan.FromMinutes(20), created);

        Assert.Equal("cancelled", (await PaymentScenario.JsonAsync(cancelled)).GetProperty("status").GetString());
        var payment = Guid.Parse((await S.TextAsync(S.TenantA, "SELECT id::text FROM payments.payments WHERE subscription_id = @id", subscription))!);
        Assert.Equal("succeeded", await S.TextAsync(S.TenantA, "SELECT status FROM payments.payments WHERE id = @id", payment));
        Assert.Equal(1, await CountAsync(S.TenantA, "SELECT count(*) FROM payments.refunds WHERE payment_id = @id AND status = 'succeeded' AND amount = 9.99 AND order_id IS NULL", payment));
        Assert.Equal(9.99m, (decimal)(await S.ScalarAsync(S.TenantA, "SELECT refunded_amount FROM payments.payments WHERE id = @id", payment))!);
        Assert.Equal(0, await CountAsync(S.TenantA, "SELECT count(*) FROM payments.outbox_messages WHERE payload->>'paymentId' = @id::text", payment));
    }

    // R2: a renewal that read the subscription before a cancel at period end cannot store over it.
    [Fact]
    public async Task A_renewal_that_read_the_subscription_before_a_cancel_cannot_store_its_outcome()
    {
        var (subscription, created) = await SubscribeAsync(trialDays: 14, "sim_success");
        await Worker.RunTimersAfterAsync(Trial + Minute, created);

        var saved = await Worker.InTenantAsync(S.TenantA, async services =>
        {
            var store = services.GetRequiredService<ISubscriptionStore>();
            var stale = (await store.FindAsync(subscription, TestContext.Current.CancellationToken))!;
            var read = RenewalState.Of(stale);
            using var cancel = await S.SendAsync(HttpMethod.Post, $"/v1/tenants/{S.TenantA}/subscriptions/{subscription}/cancel", S.CustomerA);
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            stale.ChargeFailed(SubscriptionRenewals.MaxAttempts, created);
            return await store.SaveRenewalAsync(stale, read, TestContext.Current.CancellationToken);
        });

        Assert.False(saved);
        Assert.Equal(("active", 1L), (await S.TextAsync(S.TenantA, "SELECT status FROM subscriptions.subscriptions WHERE id = @id", subscription),
            await CountAsync(S.TenantA, "SELECT count(*) FROM subscriptions.subscriptions WHERE id = @id AND cancel_at_period_end", subscription)));
    }

    // R5: a charge in review does not grant the period: the subscription is past due and the charge is never sent again.
    [Fact]
    public async Task A_renewal_charge_in_review_makes_the_subscription_past_due_without_charging_again()
    {
        var (subscription, created) = await SubscribeAsync(trialDays: 14, "sim_timeout_unknown");

        for (var minute = 1; minute <= 25; minute++)
        {
            await Worker.RunTimersAfterAsync(Trial + TimeSpan.FromMinutes(minute), created);
        }

        Assert.Equal(("past_due", 0L), (await S.TextAsync(S.TenantA, "SELECT status FROM subscriptions.subscriptions WHERE id = @id", subscription),
            await CountAsync(S.TenantA, "SELECT failed_attempts FROM subscriptions.subscriptions WHERE id = @id", subscription)));
        Assert.Equal("requires_review", await S.TextAsync(S.TenantA, "SELECT status FROM payments.payments WHERE subscription_id = @id", subscription));
        var payment = Guid.Parse((await S.TextAsync(S.TenantA, "SELECT id::text FROM payments.payments WHERE subscription_id = @id", subscription))!);
        Assert.Equal(1, _calls.GetValueOrDefault(payment));
    }

    // R9: a removed member's subscription ends at its next renewal and is never charged.
    [Fact]
    public async Task A_removed_members_subscription_ends_at_its_next_renewal_without_a_charge()
    {
        var (subscription, created) = await SubscribeAsync(trialDays: 14, "sim_success");
        var member = Guid.Parse((await S.TextAsync(S.TenantA, "SELECT subscriber_membership_id::text FROM subscriptions.subscriptions WHERE id = @id", subscription))!);
        await fixture.Harness.RemoveAsync(S.TenantA, member);

        await Worker.RunTimersAfterAsync(Trial + Minute, created);

        Assert.Equal(("cancelled", "member_removed"), (await S.TextAsync(S.TenantA, "SELECT status FROM subscriptions.subscriptions WHERE id = @id", subscription),
            await S.TextAsync(S.TenantA, "SELECT cancel_reason FROM subscriptions.subscriptions WHERE id = @id", subscription)));
        Assert.Equal(0, await CountAsync(S.TenantA, "SELECT count(*) FROM payments.payments WHERE subscription_id = @id", subscription));
    }

    // R11: renewals of a suspended tenant wait; nothing is charged.
    [Fact]
    public async Task Renewals_of_a_suspended_tenant_wait_and_charge_nothing()
    {
        var (subscription, created) = await SubscribeAsync(trialDays: 14, "sim_success");
        await fixture.Harness.SuspendTenantAsync(S.TenantA);

        await Worker.RunTimersAfterAsync(Trial + Minute, created);

        Assert.Equal("trialing", await S.TextAsync(S.TenantA, "SELECT status FROM subscriptions.subscriptions WHERE id = @id", subscription));
        Assert.Equal(0, await CountAsync(S.TenantA, "SELECT count(*) FROM payments.payments WHERE subscription_id = @id", subscription));
        Assert.Equal(created + Trial + Minute + SubscriptionRenewals.SuspendedCheckAfter, await TimerDueAsync(subscription));
    }

    // R12: after an outage the current period is charged, not every period missed in a burst.
    [Fact]
    public async Task After_an_outage_only_the_current_period_is_charged()
    {
        var (subscription, created) = await SubscribeAsync(trialDays: 14, "sim_success");
        await Worker.RunTimersAfterAsync(Trial + Minute, created);
        var anchor = await TimeAsync(S.TenantA, "SELECT anchor_at FROM subscriptions.subscriptions WHERE id = @id", subscription);

        await Worker.RunTimersAfterAsync(anchor.AddMonths(3) - created + Minute, created);

        Assert.Equal(4L, await CountAsync(S.TenantA, "SELECT period FROM subscriptions.subscriptions WHERE id = @id", subscription));
        Assert.Equal(anchor.AddMonths(4), await TimeAsync(S.TenantA, "SELECT period_end FROM subscriptions.subscriptions WHERE id = @id", subscription));
        Assert.Equal(5L, await CountAsync(S.TenantA, "SELECT sum(billing_period) FROM payments.payments WHERE subscription_id = @id", subscription));
        Assert.Equal(2L, await CountAsync(S.TenantA, "SELECT count(*) FROM payments.payments WHERE subscription_id = @id", subscription));
    }

    // ---- helpers ----

    private async Task<(Guid Subscription, DateTimeOffset Created)> SubscribeAsync(int trialDays, string paymentMethod)
    {
        using var plan = await S.SendAsync(HttpMethod.Post, $"/v1/tenants/{S.TenantA}/plans", S.AdminA,
            new { name = "Pro", price = new { amount = 9.99m, currency = "EUR" }, interval = "month", trialDays });
        var planId = (await PaymentScenario.JsonAsync(plan)).GetProperty("id").GetGuid();
        using var subscribed = await S.SendAsync(HttpMethod.Post, $"/v1/tenants/{S.TenantA}/subscriptions", S.CustomerA, new { planId, paymentMethod });
        var body = await PaymentScenario.JsonAsync(subscribed);
        return (body.GetProperty("id").GetGuid(), await TimeAsync(S.TenantA, "SELECT created_at FROM subscriptions.subscriptions WHERE id = @id", body.GetProperty("id").GetGuid()));
    }

    private Payment Charge(Guid subscription, DateTimeOffset now) =>
        Payment.StartCharge(Guid.CreateVersion7(), S.TenantA, subscription, 1, 1, Guid.CreateVersion7(), 9.99m, "EUR", "simulator", "sim_success", now);

    private Task<TimerDecision> RenewAsync(Guid subscription) =>
        Worker.InTenantAsync(S.TenantA, services => services.GetRequiredService<SubscriptionRenewer>().RenewAsync(subscription, TestContext.Current.CancellationToken));

    private async Task<long> CountAsync(Guid tenant, string sql, Guid id) =>
        Convert.ToInt64(await S.ScalarAsync(tenant, sql, id), System.Globalization.CultureInfo.InvariantCulture);

    private async Task<DateTimeOffset> TimeAsync(Guid tenant, string sql, Guid id) =>
        new((DateTime)(await S.ScalarAsync(tenant, sql, id))!, TimeSpan.Zero);

    // The due row is not under RLS; it is read through the subscription it belongs to.
    private Task<DateTimeOffset> TimerDueAsync(Guid subscription) =>
        TimeAsync(S.TenantA, "SELECT t.due_at FROM subscriptions.workflow_timers t JOIN subscriptions.subscriptions s ON s.id = t.subject_id WHERE s.id = @id", subscription);

    private async Task<bool> TimerCompletedAsync(Guid subscription) =>
        await CountAsync(S.TenantA, "SELECT count(*) FROM subscriptions.workflow_timers t JOIN subscriptions.subscriptions s ON s.id = t.subject_id WHERE s.id = @id AND t.completed_at IS NOT NULL", subscription) == 1;

    private sealed class CountingProvider(IPaymentProvider inner, ConcurrentDictionary<Guid, int> calls) : IPaymentProvider
    {
        public string Name => inner.Name;

        public Task<ProviderPaymentResult> CreateAsync(ProviderPaymentRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            calls.AddOrUpdate(request.PaymentId, 1, static (_, n) => n + 1);
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
