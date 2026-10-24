using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TenantFlow.BuildingBlocks.Messaging.Outbox;
using TenantFlow.BuildingBlocks.Messaging.RabbitMq;

namespace TenantFlow.Database.Tests.Messaging;

/// <summary>
/// SPEC-0005 criteria 4 to 8: the outbox publisher against real PostgreSQL and the local RabbitMQ. Time is a fake clock,
/// so leases and ages are exact. Each test empties the outbox first and publishes to an exchange of its own.
/// </summary>
public sealed class OutboxPublisherTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly OutboxOptions _options = new();
    private NpgsqlDataSource? _dataSource;

    private NpgsqlDataSource DataSource => _dataSource ?? throw new InvalidOperationException("Not initialized.");

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _dataSource = NpgsqlDataSource.Create(fixture.Database.AppConnectionString);
        await using var command = DataSource.CreateCommand("DELETE FROM orders.outbox_messages");
        await command.ExecuteNonQueryAsync(Cancellation);
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }
    }

    // ---- criterion 4 ----

    [Fact]
    public async Task Publishes_the_oldest_rows_in_batches_of_50_with_the_whole_envelope_and_marks_them_published()
    {
        await using var broker = await TestBroker.CreateAsync(Cancellation);
        var ids = await InsertPendingAsync(120);
        using var metrics = new OutboxMetrics(_clock);
        await using var bus = Bus(broker.Options);
        var publisher = Publisher(bus, metrics);

        var first = await publisher.PublishPendingAsync(Cancellation);
        var received = await broker.DrainAsync(Cancellation);

        Assert.Equal(50, first);
        Assert.Equal(ids.Take(50).Select(static id => id.ToString()), received.Select(static m => m.BasicProperties.MessageId));
        var message = received[0];
        Assert.Equal(("order.test.v1", "order.test.v1", "correlation-0", "application/json"),
            (message.RoutingKey, message.BasicProperties.Type, message.BasicProperties.CorrelationId, message.BasicProperties.ContentType));
        Assert.Equal(Tenant.ToString(), Header(message, RabbitMqMessageBus.TenantIdHeader));
        Assert.Equal("1", Header(message, RabbitMqMessageBus.VersionHeader));
        Assert.Equal(TraceParent, Header(message, RabbitMqMessageBus.TraceParentHeader));
        using (var body = JsonDocument.Parse(message.Body))
        {
            Assert.Equal(ids[0], body.RootElement.GetProperty("orderId").GetGuid());
        }

        Assert.Equal((50L, 70L), await CountsAsync());

        Assert.Equal(50, await publisher.PublishPendingAsync(Cancellation));
        Assert.Equal(20, await publisher.PublishPendingAsync(Cancellation));
        Assert.Equal(0, await publisher.PublishPendingAsync(Cancellation));
        Assert.Equal((120L, 0L), await CountsAsync());
        Assert.Equal(70, (await broker.DrainAsync(Cancellation)).Count);
    }

    // ---- criterion 5 ----

    [Fact]
    public async Task A_second_publisher_cannot_claim_rows_another_holds_until_the_lease_passes()
    {
        await InsertPendingAsync(60);
        var first = new OutboxStore(DataSource, "orders");
        var second = new OutboxStore(DataSource, "orders");

        var claimedFirst = await first.ClaimAsync(50, _clock.GetUtcNow(), _options.Lease, Cancellation);
        var claimedSecond = await second.ClaimAsync(50, _clock.GetUtcNow(), _options.Lease, Cancellation);
        _clock.Advance(_options.Lease);
        var afterLease = await second.ClaimAsync(100, _clock.GetUtcNow(), _options.Lease, Cancellation);

        Assert.Equal((50, 10), (claimedFirst.Count, claimedSecond.Count));
        Assert.Empty(claimedFirst.Select(static m => m.Id).Intersect(claimedSecond.Select(static m => m.Id)));
        Assert.Equal(60, afterLease.Count);
    }

    // ---- criterion 6: duplicate publication ----

    [Fact]
    public async Task A_message_published_but_never_marked_is_published_again_with_the_same_id_once_its_lease_passes()
    {
        await using var broker = await TestBroker.CreateAsync(Cancellation);
        var id = Assert.Single(await InsertPendingAsync(1));
        using var metrics = new OutboxMetrics(_clock);
        await using var bus = Bus(broker.Options);
        var publisher = Publisher(bus, metrics);

        // A publisher that crashed after the broker's confirm but before marking the row.
        var store = new OutboxStore(DataSource, "orders");
        var claimed = await store.ClaimAsync(50, _clock.GetUtcNow(), _options.Lease, Cancellation);
        Assert.Equal([id], (await bus.PublishAsync(claimed, Cancellation)).Confirmed);
        var whileHeld = await publisher.PublishPendingAsync(Cancellation);
        _clock.Advance(_options.Lease);
        var afterLease = await publisher.PublishPendingAsync(Cancellation);

        Assert.Equal((0, 1), (whileHeld, afterLease));
        var received = await broker.DrainAsync(Cancellation);
        Assert.Equal([id.ToString(), id.ToString()], received.Select(static m => m.BasicProperties.MessageId));
        Assert.Equal((1L, 0L), await CountsAsync());
    }

    // ---- criterion 7 ----

    [Fact]
    public async Task While_the_broker_is_down_rows_stay_pending_with_the_reason_and_are_published_once_it_is_back()
    {
        await using var broker = await TestBroker.CreateAsync(Cancellation);
        var id = Assert.Single(await InsertPendingAsync(1));
        using var metrics = new OutboxMetrics(_clock);
        await using var down = Bus(BrokerTestSettings.Unreachable(broker.Options));
        await using var up = Bus(broker.Options);

        var whileDown = await Publisher(down, metrics).PublishPendingAsync(Cancellation);
        var (attempts, lockedUntil, lastError) = await RowStateAsync(id);
        _clock.Advance(TimeSpan.FromSeconds(2));
        var whenBack = await Publisher(up, metrics).PublishPendingAsync(Cancellation);

        Assert.Equal((0, 1), (whileDown, whenBack));
        // Deferred for the first backoff (2 s), not left to the 30 s lease, and the reason is kept for operators.
        Assert.Equal((1, (DateTimeOffset?)(Start + TimeSpan.FromSeconds(2))), (attempts, lockedUntil));
        Assert.False(string.IsNullOrEmpty(lastError));
        Assert.Equal(id.ToString(), Assert.Single(await broker.DrainAsync(Cancellation)).BasicProperties.MessageId);
        Assert.Equal((1L, 0L), await CountsAsync());
    }

    [Fact]
    public async Task A_message_the_broker_keeps_refusing_is_deferred_with_growing_backoff_and_never_holds_back_newer_ones()
    {
        await using var broker = await TestBroker.CreateAsync(Cancellation);
        var ids = await InsertPendingAsync(2);
        using var metrics = new OutboxMetrics(_clock);
        await using var real = Bus(broker.Options);
        var publisher = Publisher(new RefusingBus(real, ids[0]), metrics);

        var first = await publisher.PublishPendingAsync(Cancellation);
        var deferredUntil = (await RowStateAsync(ids[0])).LockedUntil;
        var whileDeferred = await publisher.PublishPendingAsync(Cancellation);
        _clock.Advance(TimeSpan.FromSeconds(2));
        var retried = await publisher.PublishPendingAsync(Cancellation);
        var (attempts, lockedUntil, _) = await RowStateAsync(ids[0]);

        // The newer row is published at once although the older one, ahead of it, fails.
        Assert.Equal((1, 0, 0), (first, whileDeferred, retried));
        Assert.Equal([ids[1].ToString()], (await broker.DrainAsync(Cancellation)).Select(static m => m.BasicProperties.MessageId));
        Assert.Equal(Start + TimeSpan.FromSeconds(2), deferredUntil);
        Assert.Equal((2, (DateTimeOffset?)(Start + TimeSpan.FromSeconds(6))), (attempts, lockedUntil));
    }

    [Fact]
    public async Task Cleanup_deletes_only_rows_published_longer_ago_than_the_retention()
    {
        var old = Assert.Single(await InsertPendingAsync(1, Start - TimeSpan.FromDays(9)));
        var recent = Assert.Single(await InsertPendingAsync(1, Start - TimeSpan.FromDays(2)));
        var pending = Assert.Single(await InsertPendingAsync(1, Start - TimeSpan.FromDays(10)));
        await MarkPublishedAsync(old, Start - TimeSpan.FromDays(8));
        await MarkPublishedAsync(recent, Start - TimeSpan.FromDays(1));
        using var metrics = new OutboxMetrics(_clock);
        await using var bus = Bus(BrokerTestSettings.ForNewExchange());

        var deleted = await Publisher(bus, metrics).CleanUpAsync(Cancellation);

        Assert.Equal(1, deleted);
        Assert.Equal([pending, recent], (await RemainingIdsAsync()).Order());
    }

    // ---- criterion 8 ----

    [Fact]
    public async Task Backlog_metrics_report_the_pending_count_and_oldest_age_per_module_and_drop_to_zero_when_published()
    {
        await using var broker = await TestBroker.CreateAsync(Cancellation);
        await InsertPendingAsync(3, Start - TimeSpan.FromSeconds(90));
        using var metrics = new OutboxMetrics(_clock);
        await using var bus = Bus(broker.Options);
        var publisher = Publisher(bus, metrics);
        using var pending = new MeterCapture(OutboxMetrics.PendingGauge);
        using var age = new MeterCapture(OutboxMetrics.OldestPendingAgeGauge);

        await publisher.RefreshBacklogAsync(Cancellation);
        var before = (Observe(pending), Observe(age));
        // No refresh for 30 s (as when the database is down): the age keeps rising.
        _clock.Advance(TimeSpan.FromSeconds(30));
        var unrefreshedAge = Observe(age);
        await publisher.PublishPendingAsync(Cancellation);
        await publisher.RefreshBacklogAsync(Cancellation);
        var after = (Observe(pending), Observe(age));

        Assert.Equal((3L, 90L), before);
        Assert.Equal(120L, unrefreshedAge);
        Assert.Equal((0L, 0L), after);
    }

    [Fact]
    public async Task A_module_whose_outbox_fails_does_not_hold_back_the_modules_after_it()
    {
        await using var broker = await TestBroker.CreateAsync(Cancellation);
        await InsertPendingAsync(3);
        using var metrics = new OutboxMetrics(_clock);
        await using var bus = Bus(broker.Options);
        // A schema with no outbox table fails on every call, as a module with a missing grant or table would.
        var publisher = new OutboxPublisher(
            [new OutboxStore(DataSource, "missing_module"), new OutboxStore(DataSource, "orders")],
            bus, metrics, _clock, Options.Create(_options), NullLogger<OutboxPublisher>.Instance);

        var published = await publisher.PublishPendingAsync(Cancellation);
        await publisher.RefreshBacklogAsync(Cancellation);
        await publisher.CleanUpAsync(Cancellation);

        Assert.Equal(3, published);
        Assert.Equal(3, (await broker.DrainAsync(Cancellation)).Count);
    }

    // ---- helpers ----

    private const string TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

    private OutboxPublisher Publisher(IMessageBus bus, OutboxMetrics metrics) =>
        new([new OutboxStore(DataSource, "orders")], bus, metrics, _clock, Options.Create(_options), NullLogger<OutboxPublisher>.Instance);

    private static RabbitMqMessageBus Bus(RabbitMqOptions options) => new(new RabbitMqConnection(Options.Create(options)));

    // The real broker, except that it refuses one message every time, as it would one over its size limit.
    private sealed class RefusingBus(IMessageBus inner, Guid refused) : IMessageBus
    {
        public async Task<PublishResult> PublishAsync(IReadOnlyList<PendingMessage> messages, CancellationToken cancellationToken)
        {
            var result = await inner.PublishAsync([.. messages.Where(m => m.Id != refused)], cancellationToken);
            var refusals = messages.Where(m => m.Id == refused).Select(static m => new PublishFailure(m.Id, new InvalidOperationException("Refused by the broker.")));
            return new PublishResult(result.Confirmed, [.. result.Failed, .. refusals]);
        }
    }

    private static string? Header(RabbitMQ.Client.BasicGetResult message, string name) =>
        message.BasicProperties.Headers?[name] is byte[] bytes ? Encoding.UTF8.GetString(bytes) : null;

    // The value the gauges report for the orders module, read the way an export reads it.
    private static long Observe(MeterCapture capture)
    {
        capture.Clear();
        capture.RecordObservableInstruments();
        return Assert.Single(capture.Measurements, static m => m.Tags[OutboxMetrics.ModuleTag] == "orders").Value;
    }

    // Pending rows one millisecond apart, oldest first, as Orders writes them (ids are returned in that order).
    private async Task<List<Guid>> InsertPendingAsync(int count, DateTimeOffset? from = null)
    {
        var ids = new List<Guid>();
        var start = from ?? Start - TimeSpan.FromMinutes(1);
        for (var i = 0; i < count; i++)
        {
            var id = Guid.CreateVersion7(start);
            await using var command = DataSource.CreateCommand(
                """
                INSERT INTO orders.outbox_messages (id, tenant_id, type, version, payload, correlation_id, traceparent, occurred_at)
                VALUES (@id, @tenant, 'order.test.v1', 1, @payload::jsonb, @correlation, @traceparent, @occurred)
                """);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("tenant", Tenant);
            command.Parameters.AddWithValue("payload", $$"""{"orderId":"{{id}}"}""");
            command.Parameters.AddWithValue("correlation", $"correlation-{i}");
            command.Parameters.AddWithValue("traceparent", TraceParent);
            command.Parameters.AddWithValue("occurred", start + TimeSpan.FromMilliseconds(i));
            await command.ExecuteNonQueryAsync(Cancellation);
            ids.Add(id);
        }

        return ids;
    }

    private async Task MarkPublishedAsync(Guid id, DateTimeOffset at)
    {
        await using var command = DataSource.CreateCommand("UPDATE orders.outbox_messages SET published_at = @at WHERE id = @id");
        command.Parameters.AddWithValue("at", at);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(Cancellation);
    }

    private async Task<(long Published, long Pending)> CountsAsync()
    {
        await using var command = DataSource.CreateCommand(
            "SELECT count(*) FILTER (WHERE published_at IS NOT NULL), count(*) FILTER (WHERE published_at IS NULL) FROM orders.outbox_messages");
        await using var reader = await command.ExecuteReaderAsync(Cancellation);
        await reader.ReadAsync(Cancellation);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private async Task<(int Attempts, DateTimeOffset? LockedUntil, string? LastError)> RowStateAsync(Guid id)
    {
        await using var command = DataSource.CreateCommand("SELECT attempts, locked_until, last_error FROM orders.outbox_messages WHERE id = @id");
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(Cancellation);
        await reader.ReadAsync(Cancellation);
        return (reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private async Task<List<Guid>> RemainingIdsAsync()
    {
        await using var command = DataSource.CreateCommand("SELECT id FROM orders.outbox_messages");
        await using var reader = await command.ExecuteReaderAsync(Cancellation);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(Cancellation))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }
}
