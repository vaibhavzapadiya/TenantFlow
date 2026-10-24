using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;

namespace TenantFlow.BuildingBlocks.Messaging.RabbitMq;

/// <summary>
/// Consumes one durable queue bound to the events exchange (ADR-0021). Each delivery is read into an
/// <see cref="IncomingMessage"/>, handed to the handler inside a consumer span that continues the producer's trace, and
/// acknowledged only after the handler returns, so its effect is durable before the broker forgets the message.
/// <para>
/// A failure is retried through TTL queues (<c>&lt;queue&gt;.retry.&lt;n&gt;</c>, one per delay, each dead-lettering back
/// to the queue), so a delay survives a restart and never blocks the consumer. After the last retry, and at once for an
/// <see cref="InvalidMessageException"/>, the message goes to <c>&lt;queue&gt;.dlq</c> with the reason. A copy is published
/// with confirms before the original is acknowledged: a crash in between duplicates the message, never loses it.
/// </para>
/// </summary>
public sealed partial class RabbitMqConsumer(
    RabbitMqConnection connection,
    string queue,
    IReadOnlyCollection<string> routingKeys,
    Func<IncomingMessage, CancellationToken, Task<MessageOutcome>> handle,
    ILogger logger,
    IReadOnlyList<TimeSpan>? retryDelays = null) : IAsyncDisposable
{
    public const string ActivitySourceName = "TenantFlow.Messaging";
    public const string ConsumedCounter = "tenantflow.messaging.consumed";
    public const string RetryHeader = "x-tenantflow-retry";
    public const string DeadLetterReasonHeader = "x-tenantflow-dead-letter-reason";
    public const string DeadLetterSuffix = ".dlq";

    /// <summary>ADR-0021: first delivery plus five retries, then the dead-letter queue.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)];

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private static readonly Meter Meter = new(ActivitySourceName);
    private static readonly Counter<long> Consumed =
        Meter.CreateCounter<long>(ConsumedCounter, "{message}", "Messages a consumer finished with, by outcome: processed, duplicate, retried, dead_lettered.");

    private readonly IReadOnlyList<TimeSpan> _retryDelays = retryDelays ?? DefaultRetryDelays;
    private IChannel? _channel;
    private IChannel? _republish;
    private CancellationToken _stopping;

    public string Queue { get; } = queue;

    public string DeadLetterQueue => Queue + DeadLetterSuffix;

    /// <summary>Declares the queue, its retry and dead-letter queues and its bindings, then starts consuming.</summary>
    public async Task StartAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        var opened = await connection.GetAsync(stoppingToken);
        var channel = await opened.CreateChannelAsync(cancellationToken: stoppingToken);
        IChannel? republish = null;
        try
        {
            republish = await opened.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                stoppingToken);
            await DeclareAndConsumeAsync(channel, stoppingToken);
        }
        catch
        {
            // A failed start leaves nothing open; the caller may simply try again.
            if (republish is not null)
            {
                await republish.DisposeAsync();
            }

            await channel.DisposeAsync();
            throw;
        }

        _republish = republish;
        _channel = channel;
    }

    private async Task DeclareAndConsumeAsync(IChannel channel, CancellationToken stoppingToken)
    {
        // A small prefetch bounds the work in flight per consumer.
        await channel.BasicQosAsync(0, 10, false, stoppingToken);
        await channel.ExchangeDeclareAsync(connection.Options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: stoppingToken);
        await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);
        await channel.QueueDeclareAsync(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);
        for (var attempt = 1; attempt <= _retryDelays.Count; attempt++)
        {
            // The message waits out the delay here, then the broker routes it back to the queue through the default exchange.
            await channel.QueueDeclareAsync(RetryQueue(attempt), durable: true, exclusive: false, autoDelete: false, new Dictionary<string, object?>
            {
                ["x-message-ttl"] = (int)_retryDelays[attempt - 1].TotalMilliseconds,
                ["x-dead-letter-exchange"] = string.Empty,
                ["x-dead-letter-routing-key"] = Queue,
            }, cancellationToken: stoppingToken);
        }

        foreach (var key in routingKeys)
        {
            await channel.QueueBindAsync(Queue, connection.Options.Exchange, key, cancellationToken: stoppingToken);
        }

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += OnReceivedAsync;
        await channel.BasicConsumeAsync(Queue, autoAck: false, consumer, stoppingToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }

        if (_republish is not null)
        {
            await _republish.DisposeAsync();
        }
    }

    private string RetryQueue(int attempt) => $"{Queue}.retry.{attempt}";

    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs delivery)
    {
        var channel = ((AsyncEventingBasicConsumer)sender).Channel;
        using var activity = StartActivity(delivery);
        var retries = RetriesSoFar(delivery.BasicProperties);
        try
        {
            string outcome;
            try
            {
                var message = MessageEnvelope.Read(delivery.BasicProperties, delivery.Body);
                activity?.SetTag("messaging.message.id", message.MessageId.ToString());
                outcome = (await handle(message, _stopping)) == MessageOutcome.Processed ? "processed" : "duplicate";
            }
            catch (InvalidMessageException exception)
            {
                activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
                LogInvalid(logger, Queue, delivery.BasicProperties.MessageId, exception.Message);
                await RepublishAsync(DeadLetterQueue, delivery, retries, exception.Message);
                outcome = "dead_lettered";
            }
            catch (Exception exception) when (!_stopping.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
                if (retries < _retryDelays.Count)
                {
                    LogRetrying(logger, exception, Queue, delivery.BasicProperties.MessageId, retries + 1);
                    await RepublishAsync(RetryQueue(retries + 1), delivery, retries + 1, null);
                    outcome = "retried";
                }
                else
                {
                    // The reason names the failure's type only: exception messages can carry data.
                    LogExhausted(logger, exception, Queue, delivery.BasicProperties.MessageId, retries);
                    await RepublishAsync(DeadLetterQueue, delivery, retries, $"Retries exhausted: {exception.GetType().Name}");
                    outcome = "dead_lettered";
                }
            }

            activity?.SetTag("tenantflow.messaging.outcome", outcome);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, _stopping);
            Consumed.Add(1, new KeyValuePair<string, object?>("consumer", Queue), new KeyValuePair<string, object?>("outcome", outcome));
        }
        catch (Exception exception) when (!_stopping.IsCancellationRequested)
        {
            // Retry or dead-letter publishing failed (the broker is in trouble): leave the message to the broker.
            LogDispositionFailed(logger, exception, Queue, delivery.BasicProperties.MessageId);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, _stopping);
        }
    }

    // A copy with the original envelope, the retry count and, for the dead-letter queue, the reason. Only the envelope
    // headers are copied: the broker's own x-death bookkeeping from earlier retry hops is left behind.
    private async Task RepublishAsync(string target, BasicDeliverEventArgs delivery, int retries, string? reason)
    {
        var headers = new Dictionary<string, object?> { [RetryHeader] = retries };
        foreach (var name in RabbitMqMessageBus.EnvelopeHeaders)
        {
            if (delivery.BasicProperties.Headers?.TryGetValue(name, out var value) == true)
            {
                headers[name] = value;
            }
        }

        if (reason is not null)
        {
            headers[DeadLetterReasonHeader] = reason;
        }

        var properties = new BasicProperties(delivery.BasicProperties) { Headers = headers };

        await _republish!.BasicPublishAsync(string.Empty, target, mandatory: true, properties, delivery.Body, _stopping);
    }

    private static int RetriesSoFar(IReadOnlyBasicProperties properties) =>
        properties.Headers is { } headers && headers.TryGetValue(RetryHeader, out var value)
            ? value switch
            {
                int count => count,
                long count => (int)count,
                byte[] text when int.TryParse(System.Text.Encoding.UTF8.GetString(text), NumberStyles.None, CultureInfo.InvariantCulture, out var count) => count,
                _ => 0,
            }
            : 0;

    // The producer's traceparent travels with the message, so this span joins the trace of the request that caused it.
    private Activity? StartActivity(BasicDeliverEventArgs delivery)
    {
        ActivityContext.TryParse(MessageEnvelope.ReadHeader(delivery.BasicProperties, RabbitMqMessageBus.TraceParentHeader), null, out var parent);
        var activity = ActivitySource.StartActivity($"{Queue} process", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", Queue);
        activity?.SetTag("messaging.message.type", delivery.BasicProperties.Type);
        return activity;
    }

    [LoggerMessage(EventId = 5101, Level = LogLevel.Warning, Message = "Dead-lettered invalid message {MessageId} from {Queue}: {Reason}")]
    private static partial void LogInvalid(ILogger logger, string queue, string? messageId, string reason);

    [LoggerMessage(EventId = 5102, Level = LogLevel.Warning, Message = "Handling message {MessageId} from {Queue} failed; retry {Retry} scheduled")]
    private static partial void LogRetrying(ILogger logger, Exception exception, string queue, string? messageId, int retry);

    [LoggerMessage(EventId = 5104, Level = LogLevel.Error, Message = "Handling message {MessageId} from {Queue} failed after {Retries} retries; dead-lettered")]
    private static partial void LogExhausted(ILogger logger, Exception exception, string queue, string? messageId, int retries);

    [LoggerMessage(EventId = 5105, Level = LogLevel.Error, Message = "Retrying or dead-lettering message {MessageId} from {Queue} failed; it will be redelivered")]
    private static partial void LogDispositionFailed(ILogger logger, Exception exception, string queue, string? messageId);
}
