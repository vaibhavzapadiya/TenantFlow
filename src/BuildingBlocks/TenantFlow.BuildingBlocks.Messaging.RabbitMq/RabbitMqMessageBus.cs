using System.Globalization;
using System.Text;
using RabbitMQ.Client;
using TenantFlow.BuildingBlocks.Messaging.Outbox;

namespace TenantFlow.BuildingBlocks.Messaging.RabbitMq;

/// <summary>
/// Publishes outbox messages to the events exchange on a channel with publisher confirms (ADR-0021). A batch is written
/// in order and its confirms are awaited together, so a batch costs about one round trip rather than one per message;
/// each message counts as confirmed only once its own confirm arrived. Messages are persistent and carry the whole
/// envelope as AMQP properties and headers.
/// </summary>
public sealed class RabbitMqMessageBus(RabbitMqConnection connection) : IMessageBus, IAsyncDisposable
{
    public const string TenantIdHeader = "tenant_id";
    public const string VersionHeader = "version";
    public const string TraceParentHeader = "traceparent";

    /// <summary>The headers that make up the envelope; consumers copy exactly these when they retry or dead-letter.</summary>
    public static readonly IReadOnlyList<string> EnvelopeHeaders = [TenantIdHeader, VersionHeader, TraceParentHeader];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel? _channel;

    public async Task<PublishResult> PublishAsync(IReadOnlyList<PendingMessage> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var confirmed = new List<Guid>(messages.Count);
        var failed = new List<PublishFailure>();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            IChannel channel;
            try
            {
                channel = await ChannelAsync(cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // No channel (broker down or recovering): nothing was sent, every message failed.
                return new PublishResult([], [.. messages.Select(m => new PublishFailure(m.Id, exception))]);
            }

            // Written in order; each task completes when that message's confirm (or refusal) arrives.
            var inFlight = messages.Select(m => (m.Id, Confirm: Publish(channel, m, cancellationToken))).ToList();
            foreach (var (id, confirm) in inFlight)
            {
                try
                {
                    await confirm;
                    confirmed.Add(id);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    failed.Add(new PublishFailure(id, exception));
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return new PublishResult(confirmed, failed);
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }

        _gate.Dispose();
    }

    private Task Publish(IChannel channel, PendingMessage message, CancellationToken cancellationToken)
    {
        var properties = new BasicProperties
        {
            MessageId = message.Id.ToString(),
            Type = message.Type,
            CorrelationId = message.CorrelationId,
            Timestamp = new AmqpTimestamp(message.OccurredAt.ToUnixTimeSeconds()),
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Headers = new Dictionary<string, object?>
            {
                [TenantIdHeader] = message.TenantId.ToString(),
                [VersionHeader] = message.Version.ToString(CultureInfo.InvariantCulture),
                [TraceParentHeader] = message.TraceParent,
            },
        };

        // Not mandatory: an event no queue subscribes to yet is legitimate in publish-subscribe. Consumers declare and
        // bind their queues before they start, so a subscribed event is never dropped.
        return channel.BasicPublishAsync(connection.Options.Exchange, message.Type, mandatory: false, properties, Encoding.UTF8.GetBytes(message.Payload), cancellationToken).AsTask();
    }

    private async Task<IChannel> ChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true } open)
        {
            return open;
        }

        if (_channel is not null)
        {
            await _channel.DisposeAsync();
            _channel = null;
        }

        var opened = await connection.GetAsync(cancellationToken);
        var channel = await opened.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);
        try
        {
            await channel.ExchangeDeclareAsync(connection.Options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        }
        catch
        {
            await channel.DisposeAsync();
            throw;
        }

        _channel = channel;
        return channel;
    }
}
