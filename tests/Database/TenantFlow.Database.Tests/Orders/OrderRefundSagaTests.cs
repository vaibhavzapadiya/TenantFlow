using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.IdentityAccess.Application.TenantAccess;
using TenantFlow.Orders.Application.Orders.Services;
using TenantFlow.Payments.Application.Abstractions;

namespace TenantFlow.Database.Tests.Orders;

/// <summary>
/// SPEC-0007 criteria 13 and 14 against real PostgreSQL: the order side of refunds. Orders are paid and refunded through the
/// real API; the refund events Payments wrote to its outbox are handed to the Orders consumer exactly as stored, and the
/// cancellation refund job runs from its due row (<see cref="WorkerHarness"/>). Refund calls are counted per refund.
/// Orders cost 2 EUR a unit.
/// </summary>
public sealed class OrderRefundSagaTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
{
    private readonly ConcurrentDictionary<Guid, int> _refundCalls = new();
    private WebApplicationFactory<Program>? _factory;
    private WorkerHarness? _worker;
    private PaymentScenario? _scenario;

    private PaymentScenario S => _scenario ?? throw new InvalidOperationException("Not initialized.");

    private WorkerHarness Worker => _worker ?? throw new InvalidOperationException("Not initialized.");

    public async ValueTask InitializeAsync()
    {
        _factory = ApiFactory.CreateProduction(connectionString: fixture.Database.AppConnectionString);
        _worker = WorkerHarness.Create(fixture.Database, inner => new RefundCounter(inner, _refundCalls));
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

    // ---- criterion 13 ----

    [Fact]
    public async Task A_partial_refund_leaves_the_order_partially_refunded_and_the_rest_refunds_it_once_per_message()
    {
        var order = await PaidOrderAsync(units: 5, "sim_success");

        await RefundAsync(order, 4m);
        var partial = await LatestRefundEventAsync(order);
        var first = await HandleAsync(partial);
        var again = await HandleAsync(partial);
        var afterPartial = await StatusAsync(order);
        await RefundAsync(order, 6m);
        await HandleAsync(await LatestRefundEventAsync(order));

        Assert.Equal((MessageOutcome.Processed, MessageOutcome.Duplicate), (first, again));
        Assert.Equal(("partially_refunded", "refunded"), (afterPartial, await StatusAsync(order)));
        // A message names its tenant: in another tenant the order does not exist, so nothing moves there.
        await Assert.ThrowsAsync<InvalidMessageException>(() => HandleAsync(partial with { MessageId = Guid.CreateVersion7(), TenantId = S.TenantB }));
    }

    // ---- criterion 14 ----

    [Fact]
    public async Task Cancelling_a_paid_order_refunds_what_is_left_once_and_its_event_refunds_the_order()
    {
        var order = await PaidOrderAsync(units: 5, "sim_success");
        await RefundAsync(order, 4m);
        await HandleAsync(await LatestRefundEventAsync(order));

        using var byCustomer = await CancelAsync(S.CustomerA, order, "Changed my mind");
        using var noReason = await CancelAsync(S.AdminA, order, null);
        using var cancelled = await CancelAsync(S.AdminA, order, "Out of stock at the warehouse");
        using var again = await CancelAsync(S.AdminA, order, "Out of stock at the warehouse");
        var since = DateTimeOffset.UtcNow;
        await Worker.RunTimersAfterAsync(TimeSpan.FromSeconds(1), since);
        // The job runs again, as after a lost lease: the one cancellation refund is not sent twice.
        await Worker.InTenantAsync(S.TenantA, services => services.GetRequiredService<OrderCancellationRefunds>().RequestAsync(order, TestContext.Current.CancellationToken));
        var beforeEvent = await StatusAsync(order);
        await HandleAsync(await LatestRefundEventAsync(order));

        Assert.Equal((HttpStatusCode.Forbidden, "orders.refund_permission_required"), (byCustomer.StatusCode, await PaymentScenario.CodeAsync(byCustomer)));
        Assert.Equal((HttpStatusCode.BadRequest, "orders.cancellation_reason_invalid"), (noReason.StatusCode, await PaymentScenario.CodeAsync(noReason)));
        Assert.Equal((HttpStatusCode.Accepted, HttpStatusCode.Accepted), (cancelled.StatusCode, again.StatusCode));
        Assert.Equal("cancellation_pending", (await PaymentScenario.JsonAsync(cancelled)).GetProperty("status").GetString());
        Assert.Equal(("cancellation_pending", "refunded"), (beforeEvent, await StatusAsync(order)));
        var cancellation = Guid.Parse((await S.TextAsync(S.TenantA, "SELECT r.id::text FROM payments.refunds r JOIN payments.payments p ON p.id = r.payment_id WHERE p.order_id = @id AND r.kind = 'cancellation'", order))!);
        Assert.Equal(6m, (decimal)(await S.ScalarAsync(S.TenantA, "SELECT amount FROM payments.refunds WHERE id = @id", cancellation))!);
        Assert.Equal("Out of stock at the warehouse", await S.TextAsync(S.TenantA, "SELECT reason FROM payments.refunds WHERE id = @id", cancellation));
        Assert.Equal(1, _refundCalls.GetValueOrDefault(cancellation));
        Assert.Equal(10m, (decimal)(await S.ScalarAsync(S.TenantA, "SELECT refunded_amount FROM payments.payments WHERE order_id = @id", order))!);
        // SPEC-0009 criterion 2: the paid cancellation is audited once, though requested twice; the refused ones are not.
        Assert.Equal(1, await S.CountAsync(S.TenantA, AuditOutbox.Count("orders", "order.cancellation_requested", "order", "SELECT cancellation_requested_by FROM orders.orders WHERE id = @id"), order));
    }

    [Fact]
    public async Task A_failed_cancellation_refund_returns_the_order_to_paid()
    {
        var order = await PaidOrderAsync(units: 1, "sim_refund_decline");

        using var cancelled = await CancelAsync(S.AdminA, order, "Customer asked");
        await Worker.RunTimersAfterAsync(TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow);
        var failed = await LatestRefundEventAsync(order);
        await HandleAsync(failed);

        Assert.Equal(HttpStatusCode.Accepted, cancelled.StatusCode);
        Assert.Equal("payment.refund_failed.v1", failed.Type);
        Assert.Equal("paid", await StatusAsync(order));
    }

    // ---- review fixes ----

    // R3: after a failed cancellation refund, cancelling again sends a new refund instead of waiting forever.
    [Fact]
    public async Task Cancelling_again_after_a_failed_cancellation_refund_sends_a_new_one()
    {
        var order = await PaidOrderAsync(units: 1, "sim_refund_decline");
        for (var round = 0; round < 2; round++)
        {
            using var cancelled = await CancelAsync(S.AdminA, order, "Customer asked");
            Assert.Equal(HttpStatusCode.Accepted, cancelled.StatusCode);
            await Worker.RunTimersAfterAsync(TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow);
            await HandleAsync(await LatestRefundEventAsync(order));
        }

        Assert.Equal("paid", await StatusAsync(order));
        Assert.Equal(2, await S.CountAsync(S.TenantA, "SELECT count(*) FROM payments.refunds r JOIN payments.payments p ON p.id = r.payment_id WHERE p.order_id = @id AND r.kind = 'cancellation' AND r.status = 'failed'", order));
    }

    // R4: a cancellation waits for a pending refund to settle, then refunds exactly what is left.
    [Fact]
    public async Task A_cancellation_waits_for_a_pending_refund_and_then_refunds_exactly_the_rest()
    {
        var order = await PaidOrderAsync(units: 5, "sim_refund_timeout");
        await RefundAsync(order, 4m);
        var since = DateTimeOffset.UtcNow;
        using var cancelled = await CancelAsync(S.AdminA, order, "Out of stock");

        await Worker.RunTimersAfterAsync(TimeSpan.FromSeconds(1), since);
        var whileWaiting = await S.CountAsync(S.TenantA, "SELECT count(*) FROM payments.refunds r JOIN payments.payments p ON p.id = r.payment_id WHERE p.order_id = @id AND r.kind = 'cancellation'", order);
        for (var minute = 1; minute <= 10; minute++)
        {
            await Worker.RunTimersAfterAsync(TimeSpan.FromMinutes(minute), since);
        }

        Assert.Equal(0, whileWaiting);
        Assert.Equal(6m, (decimal)(await S.ScalarAsync(S.TenantA, "SELECT r.amount FROM payments.refunds r JOIN payments.payments p ON p.id = r.payment_id WHERE p.order_id = @id AND r.kind = 'cancellation'", order))!);
        Assert.Equal(10m, (decimal)(await S.ScalarAsync(S.TenantA, "SELECT refunded_amount FROM payments.payments WHERE order_id = @id", order))!);
        await HandleAsync(await LatestRefundEventAsync(order));
        Assert.Equal("refunded", await StatusAsync(order));
    }

    // R6 and R8: a refund event that arrives before its payment's is retried, not lost; a refunded order cannot be cancelled.
    [Fact]
    public async Task A_refund_event_before_the_payment_event_is_retried_and_a_refunded_order_cannot_be_cancelled()
    {
        var order = await S.ReservedOrderAsync(S.TenantA, onHand: 1, quantity: 1);
        await S.PayAsync(S.TenantA, order, "sim_success");
        await RefundAsync(order, 2m);
        var refunded = await LatestRefundEventAsync(order);

        await Assert.ThrowsAsync<InvalidOperationException>(() => HandleAsync(refunded));
        var unrecorded = await S.CountAsync(S.TenantA, "SELECT count(*) FROM orders.processed_messages WHERE message_id = @id", refunded.MessageId);
        await HandleAsync(await LatestEventAsync(order, "payment.succeeded.v1"));
        var outcome = await HandleAsync(refunded);
        using var cancel = await CancelAsync(S.AdminA, order, "Too late");

        Assert.Equal(0, unrecorded);
        Assert.Equal(MessageOutcome.Processed, outcome);
        Assert.Equal("refunded", await StatusAsync(order));
        Assert.Equal((HttpStatusCode.Conflict, "orders.invalid_transition"), (cancel.StatusCode, await PaymentScenario.CodeAsync(cancel)));
    }

    // ---- helpers ----

    private async Task<Guid> PaidOrderAsync(int units, string paymentMethod)
    {
        var order = await S.ReservedOrderAsync(S.TenantA, onHand: units, quantity: units);
        await S.PayAsync(S.TenantA, order, paymentMethod);
        await HandleAsync(await LatestEventAsync(order, "payment.succeeded.v1"));
        Assert.Equal("paid", await StatusAsync(order));
        return order;
    }

    private async Task RefundAsync(Guid order, decimal amount)
    {
        using var client = _factory!.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/tenants/{S.TenantA}/orders/{order}/payments/current/refunds")
        {
            Content = JsonContent.Create(new { amount, reason = "Partial" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Shared.CreateToken(S.AdminA));
        request.Headers.Add("Idempotency-Key", $"refund-{Guid.NewGuid():N}");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private Task<HttpResponseMessage> CancelAsync(string subject, Guid order, string? reason) =>
        S.SendAsync(HttpMethod.Post, $"/v1/tenants/{S.TenantA}/orders/{order}/cancel", subject, new { reason });

    private Task<IncomingMessage> LatestRefundEventAsync(Guid order) => LatestEventAsync(order, "payment.refund_%");

    // The newest event of the order in Payments' outbox, exactly as stored, as the broker would deliver it.
    private async Task<IncomingMessage> LatestEventAsync(Guid order, string typePattern)
    {
        const string Latest = "FROM payments.outbox_messages WHERE payload->>'orderId' = @id::text AND type LIKE '{0}' ORDER BY occurred_at DESC, id DESC LIMIT 1";
        var from = string.Format(System.Globalization.CultureInfo.InvariantCulture, Latest, typePattern);
        var id = Guid.Parse((await S.TextAsync(S.TenantA, "SELECT id::text " + from, order))!);
        var type = (await S.TextAsync(S.TenantA, "SELECT type " + from, order))!;
        var payload = (await S.TextAsync(S.TenantA, "SELECT payload::text " + from, order))!;
        return new IncomingMessage(id, type, 1, S.TenantA, payload, null, null);
    }

    // As the Worker does: a scope per message, its service tenant context entered from the envelope.
    private async Task<MessageOutcome> HandleAsync(IncomingMessage message)
    {
        await using var scope = Worker.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ServiceTenantContext>().Enter(message.TenantId);
        return await scope.ServiceProvider.GetRequiredService<PaymentEventConsumer>().HandleAsync(message, TestContext.Current.CancellationToken);
    }

    private Task<string?> StatusAsync(Guid order) => S.TextAsync(S.TenantA, "SELECT status FROM orders.orders WHERE id = @id", order);

    private sealed class RefundCounter(IPaymentProvider inner, ConcurrentDictionary<Guid, int> calls) : IPaymentProvider
    {
        public string Name => inner.Name;

        public Task<ProviderPaymentResult> CreateAsync(ProviderPaymentRequest request, CancellationToken cancellationToken) => inner.CreateAsync(request, cancellationToken);

        public Task<ProviderPaymentResult?> GetStatusAsync(ProviderStatusQuery query, CancellationToken cancellationToken) => inner.GetStatusAsync(query, cancellationToken);

        public Task<ProviderRefundResult> RefundAsync(ProviderRefundRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            calls.AddOrUpdate(request.RefundId, 1, static (_, n) => n + 1);
            return inner.RefundAsync(request, cancellationToken);
        }

        public Task<ProviderRefundResult?> GetRefundStatusAsync(ProviderRefundQuery query, CancellationToken cancellationToken) => inner.GetRefundStatusAsync(query, cancellationToken);
    }
}
