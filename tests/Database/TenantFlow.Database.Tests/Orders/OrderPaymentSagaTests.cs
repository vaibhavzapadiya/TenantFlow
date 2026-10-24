using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.Catalog.Infrastructure;
using TenantFlow.IdentityAccess.Application.TenantAccess;
using TenantFlow.IdentityAccess.Infrastructure;
using TenantFlow.Inventory.Infrastructure;
using TenantFlow.Orders.Application.Abstractions;
using TenantFlow.Orders.Application.Orders.Services;
using TenantFlow.Orders.Domain.Orders;
using TenantFlow.Orders.Infrastructure;
using TenantFlow.Payments.Contracts.Events;
using TenantFlow.Tenants.Infrastructure;

namespace TenantFlow.Database.Tests.Orders;

/// <summary>
/// SPEC-0006 criterion 11 against real PostgreSQL: the order payment saga. Orders are reserved through the real API;
/// payment events are handled the way the Worker handles them, each in a DI scope of its own whose service tenant context
/// is entered from the message's tenant.
/// </summary>
public sealed class OrderPaymentSagaTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private ServiceProvider? _worker;

    private WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("Not initialized.");

    private ServiceProvider Worker => _worker ?? throw new InvalidOperationException("Not initialized.");

    public ValueTask InitializeAsync()
    {
        _factory = ApiFactory.CreateProduction(connectionString: fixture.Database.AppConnectionString);
        var services = new ServiceCollection();
        var connection = fixture.Database.AppConnectionString;
        services.AddServiceTenantContext();
        services.AddTenantsModule(connection);
        services.AddCatalogModule(connection);
        services.AddInventoryModule(connection);
        services.AddOrdersModule(connection);
        _worker = services.BuildServiceProvider(validateScopes: true);
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
    public async Task Each_payment_outcome_moves_the_order_by_the_state_table_and_a_failure_gives_the_stock_back()
    {
        var s = await PaymentScenario.CreateAsync(fixture, Factory);
        var (paid, failed, review) = (await s.ReservedOrderAsync(s.TenantA), await s.ReservedOrderAsync(s.TenantA), await s.ReservedOrderAsync(s.TenantA));

        var outcomes = new[]
        {
            await HandleAsync(Succeeded(s.TenantA, paid)),
            await HandleAsync(Failed(s.TenantA, failed)),
            await HandleAsync(ReviewRequired(s.TenantA, review)),
        };
        var reviewed = await StatusAsync(s, review);
        var settled = await HandleAsync(Succeeded(s.TenantA, review));

        Assert.All(outcomes.Append(settled), static o => Assert.Equal(MessageOutcome.Processed, o));
        Assert.Equal(("paid", "payment_failed", "payment_review", "paid"),
            (await StatusAsync(s, paid), await StatusAsync(s, failed), reviewed, await StatusAsync(s, review)));
        // A paid order keeps its units (fulfilment comes later); a failed one has given them back.
        Assert.Equal((1L, 0L), (await HeldAsync(s, paid), await HeldAsync(s, failed)));
    }

    [Fact]
    public async Task A_duplicate_has_one_effect_and_a_redelivery_after_a_crash_before_the_release_still_releases()
    {
        var s = await PaymentScenario.CreateAsync(fixture, Factory);
        var concurrent = await s.ReservedOrderAsync(s.TenantA);
        var crashed = await s.ReservedOrderAsync(s.TenantA);
        var message = Failed(s.TenantA, concurrent);
        var interrupted = Failed(s.TenantA, crashed);

        var both = await Task.WhenAll(HandleAsync(message), HandleAsync(message));
        // A crash right after the state change committed: the message is recorded and the order failed, the stock still held.
        await InTenantAsync(s.TenantA, scope => scope.GetRequiredService<IOrderStore>()
            .ApplyMessageAsync(crashed, OrderTrigger.PaymentFailed, PaymentEventConsumer.Consumer, interrupted.MessageId, TestContext.Current.CancellationToken));
        var heldBeforeRedelivery = await HeldAsync(s, crashed);
        var redelivered = await HandleAsync(interrupted);

        Assert.Equal([MessageOutcome.Processed, MessageOutcome.Duplicate], both.Order());
        Assert.Equal(0L, await HeldAsync(s, concurrent));
        Assert.Equal((1L, MessageOutcome.Duplicate, 0L), (heldBeforeRedelivery, redelivered, await HeldAsync(s, crashed)));
        Assert.Equal("payment_failed", await StatusAsync(s, crashed));
    }

    [Fact]
    public async Task An_event_the_order_state_does_not_allow_is_acknowledged_and_changes_nothing()
    {
        var s = await PaymentScenario.CreateAsync(fixture, Factory);
        var order = await s.ReservedOrderAsync(s.TenantA);
        await HandleAsync(Succeeded(s.TenantA, order));

        // Out of order after the success: a review request, and a failure that must not give a paid order's stock back.
        var late = await HandleAsync(ReviewRequired(s.TenantA, order));
        var contradicting = await HandleAsync(Failed(s.TenantA, order));

        Assert.Equal((MessageOutcome.Processed, MessageOutcome.Processed), (late, contradicting));
        Assert.Equal("paid", await StatusAsync(s, order));
        Assert.Equal(1L, await HeldAsync(s, order));
    }

    [Fact]
    public async Task A_message_acts_in_its_own_tenant_only_and_an_unknown_tenant_or_order_is_dead_lettered()
    {
        var s = await PaymentScenario.CreateAsync(fixture, Factory);
        var aOrder = await s.ReservedOrderAsync(s.TenantA);
        var bOrder = await s.ReservedOrderAsync(s.TenantB);

        // Tenant B's message naming tenant A's order; then B's own order; then a tenant that does not exist.
        var crossTenant = await Assert.ThrowsAsync<InvalidMessageException>(() => HandleAsync(Succeeded(s.TenantB, aOrder)));
        var own = await HandleAsync(Succeeded(s.TenantB, bOrder));
        var unknownTenant = await Assert.ThrowsAsync<InvalidMessageException>(() => HandleAsync(Succeeded(Guid.CreateVersion7(), aOrder)));

        Assert.Equal(("Unknown order.", "Unknown tenant."), (crossTenant.Message, unknownTenant.Message));
        Assert.Equal(MessageOutcome.Processed, own);
        Assert.Equal(("pending_payment", "paid"), (await StatusAsync(s, aOrder), await StatusAsync(s, bOrder, s.TenantB)));
        Assert.Equal(0L, await s.CountAsync(s.TenantB, "SELECT count(*) FROM orders.processed_messages WHERE tenant_id = @id", s.TenantA));
        Assert.Equal(0L, await s.CountAsync(s.TenantA, "SELECT count(*) FROM orders.processed_messages WHERE tenant_id = @id", s.TenantA));
    }

    // ---- helpers ----

    // As the Worker does: a scope per message, its service tenant context entered from the envelope.
    private async Task<MessageOutcome> HandleAsync(IncomingMessage message)
    {
        await using var scope = Worker.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ServiceTenantContext>().Enter(message.TenantId);
        return await scope.ServiceProvider.GetRequiredService<PaymentEventConsumer>().HandleAsync(message, TestContext.Current.CancellationToken);
    }

    private async Task InTenantAsync<T>(Guid tenant, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Worker.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ServiceTenantContext>().Enter(tenant);
        await action(scope.ServiceProvider);
    }

    private static IncomingMessage Succeeded(Guid tenant, Guid order) =>
        Message(PaymentSucceededV1.EventType, tenant, new PaymentSucceededV1(Guid.CreateVersion7(), order, new PaymentAmount(2m, "EUR")));

    private static IncomingMessage Failed(Guid tenant, Guid order) =>
        Message(PaymentFailedV1.EventType, tenant, new PaymentFailedV1(Guid.CreateVersion7(), order, new PaymentAmount(2m, "EUR"), "card_declined"));

    private static IncomingMessage ReviewRequired(Guid tenant, Guid order) =>
        Message(PaymentReviewRequiredV1.EventType, tenant, new PaymentReviewRequiredV1(Guid.CreateVersion7(), order, new PaymentAmount(2m, "EUR")));

    private static IncomingMessage Message<TEvent>(string type, Guid tenant, TEvent payload) =>
        new(Guid.CreateVersion7(), type, 1, tenant, JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), null, null);

    private static Task<string?> StatusAsync(PaymentScenario s, Guid order, Guid? tenant = null) =>
        s.TextAsync(tenant ?? s.TenantA, "SELECT status FROM orders.orders WHERE id = @id", order);

    // Units the order still holds in Inventory.
    private static async Task<long> HeldAsync(PaymentScenario s, Guid order) =>
        await s.CountAsync(s.TenantA, "SELECT coalesce(sum(quantity), 0) FROM inventory.reservations WHERE order_id = @id AND status = 'reserved'", order);
}
