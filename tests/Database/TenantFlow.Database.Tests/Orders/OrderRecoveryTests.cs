using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TenantFlow.Orders.Application.Abstractions;
using TenantFlow.Orders.Application.Orders.Services;
using TenantFlow.Orders.Domain.Orders;

namespace TenantFlow.Database.Tests.Orders;

/// <summary>
/// SPEC-0006 criterion 13 against real PostgreSQL: the order workflow's deadlines, run by the Worker's timers. An
/// interrupted submit is finished; a reserved order nobody started paying for is cancelled and its stock released after
/// the payment window; a begun payment is never expired.
/// </summary>
public sealed class OrderRecoveryTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private WorkerHarness? _worker;

    private WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("Not initialized.");

    private WorkerHarness Worker => _worker ?? throw new InvalidOperationException("Not initialized.");

    public ValueTask InitializeAsync()
    {
        _factory = ApiFactory.CreateProduction(connectionString: fixture.Database.AppConnectionString);
        _worker = WorkerHarness.Create(fixture.Database);
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
    public async Task A_submit_interrupted_after_its_first_step_is_finished_by_its_deadline()
    {
        var s = await PaymentScenario.CreateAsync(fixture, Factory);
        var since = DateTimeOffset.UtcNow;
        var order = await s.ReservedOrderAsync(s.TenantA, onHand: 2);
        var interrupted = await DraftAsync(s);
        // The first committed step of a submit, then a crash: the order waits in pending_reservation with its deadline set.
        Assert.True(await Worker.InTenantAsync(s.TenantA, sp => sp.GetRequiredService<IOrderStore>()
            .TryTransitionAsync(interrupted, OrderStatus.Draft, OrderStatus.PendingReservation, null, null, null, TestContext.Current.CancellationToken)));

        await Worker.RunTimersAfterAsync(TimeSpan.FromMinutes(2), since);
        var beforeDeadline = await StatusAsync(s, interrupted);
        await Worker.RunTimersAfterAsync(OrderDeadlines.ResumeSubmitAfter + TimeSpan.FromMinutes(1), since);

        Assert.Equal(("pending_reservation", "pending_payment"), (beforeDeadline, await StatusAsync(s, interrupted)));
        Assert.Equal(1L, await HeldAsync(s, interrupted));
        Assert.Equal("pending_payment", await StatusAsync(s, order));
    }

    [Fact]
    public async Task An_unpaid_order_expires_after_the_payment_window_and_one_whose_payment_began_never_does()
    {
        var s = await PaymentScenario.CreateAsync(fixture, Factory);
        var since = DateTimeOffset.UtcNow;
        var unpaid = await s.ReservedOrderAsync(s.TenantA);
        var paying = await s.ReservedOrderAsync(s.TenantA);
        await s.PayAsync(s.TenantA, paying, "sim_timeout_unknown");

        await Worker.RunTimersAfterAsync(OrderDeadlines.PaymentWindow - TimeSpan.FromMinutes(2), since);
        var beforeWindow = await StatusAsync(s, unpaid);
        await Worker.RunTimersAfterAsync(OrderDeadlines.PaymentWindow + TimeSpan.FromMinutes(1), since);

        Assert.Equal(("pending_payment", "cancelled"), (beforeWindow, await StatusAsync(s, unpaid)));
        Assert.Equal(0L, await HeldAsync(s, unpaid));
        Assert.Equal(1L, await s.CountAsync(s.TenantA, "SELECT count(*) FROM orders.outbox_messages WHERE type = 'order.cancelled.v1' AND payload->>'orderId' = @id::text", unpaid));
        Assert.Equal(("pending_payment", 1L), (await StatusAsync(s, paying), await HeldAsync(s, paying)));
    }

    [Fact]
    public async Task An_expiry_that_crashed_before_the_release_gives_the_stock_back_when_it_runs_again()
    {
        var s = await PaymentScenario.CreateAsync(fixture, Factory);
        var order = await s.ReservedOrderAsync(s.TenantA);
        // As a crash right after the cancel committed would leave it: cancelled, units still held.
        await s.ScalarAsync(s.TenantA, "UPDATE orders.orders SET status = 'cancelled' WHERE id = @id", order);

        await Worker.InTenantAsync(s.TenantA, sp => sp.GetRequiredService<OrderRecovery>().ExpireUnpaidAsync(order, TestContext.Current.CancellationToken));

        Assert.Equal(0L, await HeldAsync(s, order));
    }

    [Fact]
    public async Task A_deadline_acts_in_its_own_tenant_only()
    {
        var s = await PaymentScenario.CreateAsync(fixture, Factory);
        var since = DateTimeOffset.UtcNow;
        var aOrder = await s.ReservedOrderAsync(s.TenantA);
        // A timer that names tenant B but tenant A's order: it must change nothing.
        await s.ScalarAsync(s.TenantA, "UPDATE orders.workflow_timers SET tenant_id = '" + s.TenantB + "' WHERE subject_id = @id", aOrder);

        await Worker.RunTimersAfterAsync(OrderDeadlines.PaymentWindow + TimeSpan.FromMinutes(1), since);

        Assert.Equal(("pending_payment", 1L), (await StatusAsync(s, aOrder), await HeldAsync(s, aOrder)));
    }

    private static async Task<Guid> DraftAsync(PaymentScenario s)
    {
        using var product = await s.SendAsync(HttpMethod.Get, $"/v1/tenants/{s.TenantA}/products", s.AdminA);
        var productId = (await PaymentScenario.JsonAsync(product)).GetProperty("items")[0].GetProperty("id").GetGuid();
        using var order = await s.SendAsync(HttpMethod.Post, $"/v1/tenants/{s.TenantA}/orders", s.CustomerA, new { lines = new[] { new { productId, quantity = 1 } } });
        return Guid.Parse(order.Headers.Location!.ToString().Split('/')[^1]);
    }

    private static Task<string?> StatusAsync(PaymentScenario s, Guid order) =>
        s.TextAsync(s.TenantA, "SELECT status FROM orders.orders WHERE id = @id", order);

    private static Task<long> HeldAsync(PaymentScenario s, Guid order) =>
        s.CountAsync(s.TenantA, "SELECT coalesce(sum(quantity), 0) FROM inventory.reservations WHERE order_id = @id AND status = 'reserved'", order);
}
