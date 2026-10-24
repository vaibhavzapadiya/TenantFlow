using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.BuildingBlocks.Messaging.Outbox;
using TenantFlow.BuildingBlocks.Messaging.RabbitMq;
using TenantFlow.BuildingBlocks.Persistence;
using TenantFlow.Notifications.Application.Notifications.Services;
using TenantFlow.Notifications.Infrastructure;
using TenantFlow.Orders.Contracts.Events;
using TenantFlow.Tenants.Infrastructure;

namespace TenantFlow.Database.Tests.Messaging;

/// <summary>
/// SPEC-0005 criteria 9, 10 and 13: the Notifications consumer against real PostgreSQL and the local RabbitMQ. Messages
/// are handled the way the Worker handles them: in a DI scope of their own.
/// </summary>
public sealed class NotificationConsumerTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
{
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(15);

    private ServiceProvider? _services;

    private ServiceProvider Services => _services ?? throw new InvalidOperationException("Not initialized.");

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddTenantsModule(fixture.Database.AppConnectionString);
        services.AddNotificationsModule(fixture.Database.AppConnectionString);
        _services = services.BuildServiceProvider();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }
    }

    // ---- criterion 9: duplicate consumption ----

    [Fact]
    public async Task A_message_delivered_twice_through_the_broker_records_one_notification()
    {
        var tenant = await fixture.Harness.CreateTenantAsync("Consumer tenant");
        var order = Guid.NewGuid();
        await using var broker = await TestBroker.CreateAsync(Cancellation);
        var outcomes = Channel.CreateUnbounded<MessageOutcome>();
        await using var connection = new RabbitMqConnection(Options.Create(broker.Options));
        await using var consumer = new RabbitMqConsumer(connection, broker.NewQueueName(), OrderEventConsumer.EventTypes, Recording(outcomes), NullLogger.Instance);
        await consumer.StartAsync(Cancellation);
        await using var bus = new RabbitMqMessageBus(connection);
        var message = Reserved(tenant, order, traceParent: null);

        // Published twice, as a publisher that crashed before marking the row would.
        Assert.Single((await bus.PublishAsync([message], Cancellation)).Confirmed);
        Assert.Single((await bus.PublishAsync([message], Cancellation)).Confirmed);

        Assert.Equal([MessageOutcome.Processed, MessageOutcome.Duplicate], [await NextAsync(outcomes), await NextAsync(outcomes)]);
        Assert.Equal((1L, 1L), await CountsAsync(tenant, order, message.Id));
    }

    [Fact]
    public async Task Two_concurrent_deliveries_of_one_message_record_one_notification()
    {
        var tenant = await fixture.Harness.CreateTenantAsync("Consumer tenant");
        var order = Guid.NewGuid();
        var message = Incoming(Reserved(tenant, order, traceParent: null));

        var outcomes = await Task.WhenAll(HandleAsync(message, Cancellation), HandleAsync(message, Cancellation));

        Assert.Equal([MessageOutcome.Processed, MessageOutcome.Duplicate], outcomes.Order());
        Assert.Equal((1L, 1L), await CountsAsync(tenant, order, message.MessageId));
    }

    // ---- criterion 10 ----

    [Fact]
    public async Task Each_message_is_recorded_in_its_own_tenant_only_never_in_the_previous_ones()
    {
        var tenantA = await fixture.Harness.CreateTenantAsync("Tenant A");
        var tenantB = await fixture.Harness.CreateTenantAsync("Tenant B");
        var (orderB, orderA) = (Guid.NewGuid(), Guid.NewGuid());
        var forB = Incoming(Reserved(tenantB, orderB, traceParent: null));
        var forA = Incoming(Reserved(tenantA, orderA, traceParent: null));

        // B first, then A, through the same handler path: A's message must not land in B.
        Assert.Equal(MessageOutcome.Processed, await HandleAsync(forB, Cancellation));
        Assert.Equal(MessageOutcome.Processed, await HandleAsync(forA, Cancellation));

        Assert.Equal((1L, 1L), await CountsAsync(tenantB, orderB, forB.MessageId));
        Assert.Equal((0L, 0L), await CountsAsync(tenantA, orderB, forB.MessageId));
        Assert.Equal((1L, 1L), await CountsAsync(tenantA, orderA, forA.MessageId));
        Assert.Equal((0L, 0L), await CountsAsync(tenantB, orderA, forA.MessageId));
    }

    [Fact]
    public async Task The_consumer_keeps_consuming_after_the_broker_drops_its_connection()
    {
        var tenant = await fixture.Harness.CreateTenantAsync("Consumer tenant");
        await using var broker = await TestBroker.CreateAsync(Cancellation);
        var outcomes = Channel.CreateUnbounded<MessageOutcome>();
        await using var connection = new RabbitMqConnection(Options.Create(broker.Options));
        await using var consumer = new RabbitMqConsumer(connection, broker.NewQueueName(), OrderEventConsumer.EventTypes, Recording(outcomes), NullLogger.Instance);
        await consumer.StartAsync(Cancellation);
        await using var bus = new RabbitMqMessageBus(connection);
        Assert.Single((await bus.PublishAsync([Reserved(tenant, Guid.NewGuid(), traceParent: null)], Cancellation)).Confirmed);
        Assert.Equal(MessageOutcome.Processed, await NextAsync(outcomes));

        // As a broker restart would. The publisher keeps using the same connection while it recovers.
        await broker.CloseClientConnectionsAsync(DeliveryTimeout, Cancellation);
        var after = Reserved(tenant, Guid.NewGuid(), traceParent: null);
        var deadline = DateTimeOffset.UtcNow + DeliveryTimeout;
        while ((await bus.PublishAsync([after], Cancellation)).Confirmed.Count == 0)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The connection never recovered.");
            await Task.Delay(TimeSpan.FromMilliseconds(500), Cancellation);
        }

        Assert.Equal(MessageOutcome.Processed, await NextAsync(outcomes));
    }

    // ---- criterion 13 ----

    [Fact]
    public async Task The_consumer_span_continues_the_trace_of_the_request_that_published_the_message()
    {
        var tenant = await fixture.Harness.CreateTenantAsync("Consumer tenant");
        var (traceId, spanId) = (ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom());
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == RabbitMqConsumer.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (spans) { spans.Add(activity); } },
        };
        ActivitySource.AddActivityListener(listener);
        await using var broker = await TestBroker.CreateAsync(Cancellation);
        var outcomes = Channel.CreateUnbounded<MessageOutcome>();
        await using var connection = new RabbitMqConnection(Options.Create(broker.Options));
        await using var consumer = new RabbitMqConsumer(connection, broker.NewQueueName(), OrderEventConsumer.EventTypes, Recording(outcomes), NullLogger.Instance);
        await consumer.StartAsync(Cancellation);
        await using var bus = new RabbitMqMessageBus(connection);

        Assert.Single((await bus.PublishAsync([Reserved(tenant, Guid.NewGuid(), $"00-{traceId}-{spanId}-01")], Cancellation)).Confirmed);
        Assert.Equal(MessageOutcome.Processed, await NextAsync(outcomes));

        // The handler reports its outcome before the consumer span ends (the ack comes first), so wait for the span.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (!HasSpan() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), Cancellation);
        }

        Activity span;
        lock (spans)
        {
            span = Assert.Single(spans, s => s.TraceId == traceId);
        }

        Assert.Equal((ActivityKind.Consumer, spanId), (span.Kind, span.ParentSpanId));

        bool HasSpan()
        {
            lock (spans)
            {
                return spans.Exists(s => s.TraceId == traceId);
            }
        }
    }

    // ---- helpers ----

    private static PendingMessage Reserved(Guid tenant, Guid order, string? traceParent)
    {
        var payload = OutgoingMessage.From(new OrderReservedV1(order, Guid.NewGuid(), new OrderTotal(10m, "EUR"), 1));
        return new PendingMessage(Guid.CreateVersion7(), tenant, payload.Type, payload.Version, payload.Payload, Guid.NewGuid().ToString("N"), traceParent, DateTimeOffset.UtcNow);
    }

    private static IncomingMessage Incoming(PendingMessage m) => new(m.Id, m.Type, m.Version, m.TenantId, m.Payload, m.CorrelationId, m.TraceParent);

    // As the Worker does it: a fresh scope, so nothing carries over from one message to the next.
    private async Task<MessageOutcome> HandleAsync(IncomingMessage message, CancellationToken cancellationToken)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrderEventConsumer>().HandleAsync(message, cancellationToken);
    }

    private Func<IncomingMessage, CancellationToken, Task<MessageOutcome>> Recording(Channel<MessageOutcome> outcomes) =>
        async (message, cancellationToken) =>
        {
            var outcome = await HandleAsync(message, cancellationToken);
            await outcomes.Writer.WriteAsync(outcome, cancellationToken);
            return outcome;
        };

    private static async Task<MessageOutcome> NextAsync(Channel<MessageOutcome> outcomes)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(DeliveryTimeout);
        return await outcomes.Reader.ReadAsync(timeout.Token);
    }

    // Notifications for the order and processed rows for the message, as the application role inside the tenant's scope.
    private async Task<(long Notifications, long Processed)> CountsAsync(Guid tenant, Guid order, Guid messageId)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var transaction = await connection.BeginTransactionAsync(Cancellation);
        await using (var set = new NpgsqlCommand($"SELECT set_config('{TenantScopedTransaction.SettingName}', @tenant, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("tenant", tenant.ToString());
            await set.ExecuteNonQueryAsync(Cancellation);
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT (SELECT count(*) FROM notifications.notifications WHERE order_id = @order),
                   (SELECT count(*) FROM notifications.processed_messages WHERE message_id = @message)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("order", order);
        command.Parameters.AddWithValue("message", messageId);
        await using var reader = await command.ExecuteReaderAsync(Cancellation);
        await reader.ReadAsync(Cancellation);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }
}
