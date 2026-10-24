using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using RabbitMQ.Client;
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
/// SPEC-0005 criteria 11, 12 and 14: invalid messages are dead-lettered at once, transient failures are retried through the
/// retry queues and then dead-lettered, and every outcome is counted. Retry delays are shortened to keep the tests fast;
/// the retry count is the real one (five).
/// </summary>
public sealed class DeadLetterTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private static readonly IReadOnlyList<TimeSpan> FastRetries = [.. Enumerable.Repeat(TimeSpan.FromMilliseconds(100), 5)];

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

    // ---- criteria 11 and 14 ----

    [Fact]
    public async Task Invalid_messages_go_straight_to_the_dead_letter_queue_with_the_reason_and_write_nothing()
    {
        var tenant = await fixture.Harness.CreateTenantAsync("Dead-letter tenant");
        await using var broker = await TestBroker.CreateAsync(Cancellation);
        await using var connection = new RabbitMqConnection(Options.Create(broker.Options));
        await using var consumer = new RabbitMqConsumer(connection, broker.NewQueueName(), OrderEventConsumer.EventTypes, HandleAsync, NullLogger.Instance, FastRetries);
        using var consumed = new MeterCapture(RabbitMqConsumer.ConsumedCounter);
        await consumer.StartAsync(Cancellation);
        var valid = Reserved(tenant);

        await broker.PublishRawAsync(valid.Type, Properties(valid, tenant: null), valid.Payload, Cancellation);
        await broker.PublishRawAsync(valid.Type, Properties(valid, tenant: Guid.NewGuid()), valid.Payload, Cancellation);
        await broker.PublishRawAsync(valid.Type, Properties(valid, tenant, type: "order.unknown.v1"), valid.Payload, Cancellation);
        await broker.PublishRawAsync(valid.Type, Properties(valid, tenant), "not json", Cancellation);
        var dead = await broker.WaitForAsync(consumer.DeadLetterQueue, 4, Wait, Cancellation);

        Assert.Equal(
            ["Missing or malformed tenant id.", "Unknown message type.", "Unknown tenant.", "Unreadable payload."],
            dead.Select(static m => Header(m, RabbitMqConsumer.DeadLetterReasonHeader)).Order());
        // Never retried, and the original envelope travels with it for the replay procedure.
        Assert.All(dead, m => Assert.Equal((0, valid.Id.ToString()), (Retries(m), m.BasicProperties.MessageId)));
        Assert.Equal(0L, await NotificationCountAsync(tenant));
        Assert.Equal([4L, 0L], [Count(consumed, consumer.Queue, "dead_lettered"), Count(consumed, consumer.Queue, "retried")]);
    }

    // ---- criteria 12 and 14 ----

    [Fact]
    public async Task A_transient_failure_is_retried_and_the_retry_that_succeeds_records_the_notification_once()
    {
        var tenant = await fixture.Harness.CreateTenantAsync("Retry tenant");
        var calls = 0;
        var done = new TaskCompletionSource<MessageOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var broker = await TestBroker.CreateAsync(Cancellation);
        await using var connection = new RabbitMqConnection(Options.Create(broker.Options));
        // The database is "down" for the first two deliveries.
        await using var consumer = new RabbitMqConsumer(connection, broker.NewQueueName(), OrderEventConsumer.EventTypes, async (message, cancellationToken) =>
        {
            if (Interlocked.Increment(ref calls) <= 2)
            {
                throw new InvalidOperationException("Simulated outage.");
            }

            var outcome = await HandleAsync(message, cancellationToken);
            done.TrySetResult(outcome);
            return outcome;
        }, NullLogger.Instance, FastRetries);
        using var consumed = new MeterCapture(RabbitMqConsumer.ConsumedCounter);
        await consumer.StartAsync(Cancellation);
        await using var bus = new RabbitMqMessageBus(connection);

        Assert.Single((await bus.PublishAsync([Reserved(tenant)], Cancellation)).Confirmed);

        Assert.Equal(MessageOutcome.Processed, await done.Task.WaitAsync(Wait, Cancellation));
        Assert.Equal(3, calls);
        Assert.Equal(1L, await NotificationCountAsync(tenant));
        Assert.Empty(await broker.WaitForAsync(consumer.DeadLetterQueue, 1, TimeSpan.FromMilliseconds(500), Cancellation));
        Assert.Equal([2L, 1L], [Count(consumed, consumer.Queue, "retried"), Count(consumed, consumer.Queue, "processed")]);
    }

    [Fact]
    public async Task A_message_that_keeps_failing_is_dead_lettered_after_five_retries()
    {
        var tenant = await fixture.Harness.CreateTenantAsync("Retry tenant");
        var calls = 0;
        await using var broker = await TestBroker.CreateAsync(Cancellation);
        await using var connection = new RabbitMqConnection(Options.Create(broker.Options));
        await using var consumer = new RabbitMqConsumer(connection, broker.NewQueueName(), OrderEventConsumer.EventTypes, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("Simulated outage.");
        }, NullLogger.Instance, FastRetries);
        using var consumed = new MeterCapture(RabbitMqConsumer.ConsumedCounter);
        await consumer.StartAsync(Cancellation);
        await using var bus = new RabbitMqMessageBus(connection);
        var message = Reserved(tenant);

        Assert.Single((await bus.PublishAsync([message], Cancellation)).Confirmed);
        var dead = Assert.Single(await broker.WaitForAsync(consumer.DeadLetterQueue, 1, Wait, Cancellation));

        Assert.Equal((message.Id.ToString(), 5, "Retries exhausted: InvalidOperationException"),
            (dead.BasicProperties.MessageId, Retries(dead), Header(dead, RabbitMqConsumer.DeadLetterReasonHeader)));
        Assert.Equal(6, calls);
        Assert.Equal([5L, 1L], [Count(consumed, consumer.Queue, "retried"), Count(consumed, consumer.Queue, "dead_lettered")]);
    }

    // ---- helpers ----

    private static PendingMessage Reserved(Guid tenant)
    {
        var payload = OutgoingMessage.From(new OrderReservedV1(Guid.NewGuid(), Guid.NewGuid(), new OrderTotal(10m, "EUR"), 1));
        return new PendingMessage(Guid.CreateVersion7(), tenant, payload.Type, payload.Version, payload.Payload, Guid.NewGuid().ToString("N"), null, DateTimeOffset.UtcNow);
    }

    // The envelope the real publisher writes, with the tenant or type replaced.
    private static BasicProperties Properties(PendingMessage message, Guid? tenant, string? type = null)
    {
        var headers = new Dictionary<string, object?> { [RabbitMqMessageBus.VersionHeader] = "1" };
        if (tenant is not null)
        {
            headers[RabbitMqMessageBus.TenantIdHeader] = tenant.Value.ToString();
        }

        return new BasicProperties { MessageId = message.Id.ToString(), Type = type ?? message.Type, Headers = headers, DeliveryMode = DeliveryModes.Persistent };
    }

    private async Task<MessageOutcome> HandleAsync(IncomingMessage message, CancellationToken cancellationToken)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrderEventConsumer>().HandleAsync(message, cancellationToken);
    }

    private static string? Header(BasicGetResult message, string name) =>
        message.BasicProperties.Headers?[name] is byte[] bytes ? Encoding.UTF8.GetString(bytes) : null;

    private static int Retries(BasicGetResult message) =>
        message.BasicProperties.Headers?.TryGetValue(RabbitMqConsumer.RetryHeader, out var value) == true ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) : -1;

    private static long Count(MeterCapture capture, string consumer, string outcome) =>
        capture.Measurements.Where(m => m.Tags["consumer"] == consumer && m.Tags["outcome"] == outcome).Sum(static m => m.Value);

    private async Task<long> NotificationCountAsync(Guid tenant)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var transaction = await connection.BeginTransactionAsync(Cancellation);
        await using (var set = new NpgsqlCommand($"SELECT set_config('{TenantScopedTransaction.SettingName}', @tenant, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("tenant", tenant.ToString());
            await set.ExecuteNonQueryAsync(Cancellation);
        }

        await using var command = new NpgsqlCommand("SELECT count(*) FROM notifications.notifications", connection, transaction);
        return (long)(await command.ExecuteScalarAsync(Cancellation))!;
    }
}
