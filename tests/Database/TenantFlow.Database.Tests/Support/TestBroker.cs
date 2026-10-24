using RabbitMQ.Client;
using TenantFlow.BuildingBlocks.Messaging.RabbitMq;

namespace TenantFlow.Database.Tests.Support;

/// <summary>
/// A test's own exchange on the local RabbitMQ, with one exclusive queue bound to every routing key, so the test can read
/// exactly what was published. The exchange is deleted afterwards.
/// </summary>
internal sealed class TestBroker : IAsyncDisposable
{
    private readonly IConnection _connection;
    private readonly IChannel _channel;
    private readonly string _queue;
    private readonly List<string> _queues = [];

    private TestBroker(RabbitMqOptions options, IConnection connection, IChannel channel, string queue)
    {
        Options = options;
        _connection = connection;
        _channel = channel;
        _queue = queue;
    }

    public RabbitMqOptions Options { get; }

    public static async Task<TestBroker> CreateAsync(CancellationToken cancellationToken)
    {
        var options = BrokerTestSettings.ForNewExchange();
        var factory = new ConnectionFactory
        {
            HostName = options.Host!,
            Port = options.Port,
            UserName = options.Username!,
            Password = options.Password!,
            // Named apart from the connections under test, so closing those never closes this one.
            ClientProvidedName = options.ClientName + "-observer",
        };
        var connection = await factory.CreateConnectionAsync(cancellationToken);
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        // Declared as the publisher declares it, so either side may create it first.
        await channel.ExchangeDeclareAsync(options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        var queue = await channel.QueueDeclareAsync(string.Empty, durable: false, exclusive: true, autoDelete: true, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(queue.QueueName, options.Exchange, "#", cancellationToken: cancellationToken);
        return new TestBroker(options, connection, channel, queue.QueueName);
    }

    /// <summary>A queue name for a consumer under test; the queue is deleted with the exchange.</summary>
    public string NewQueueName()
    {
        var name = $"{Options.Exchange}.queue.{_queues.Count}";
        _queues.Add(name);
        return name;
    }

    /// <summary>Publishes a message as-is to the test's exchange, for envelopes the real publisher would never produce.</summary>
    public async Task PublishRawAsync(string routingKey, BasicProperties properties, string body, CancellationToken cancellationToken) =>
        await _channel.BasicPublishAsync(Options.Exchange, routingKey, mandatory: false, properties, System.Text.Encoding.UTF8.GetBytes(body), cancellationToken);

    /// <summary>Waits until <paramref name="queue"/> has delivered <paramref name="count"/> messages, and returns them.</summary>
    public async Task<IReadOnlyList<BasicGetResult>> WaitForAsync(string queue, int count, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var messages = new List<BasicGetResult>();
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (messages.Count < count && DateTimeOffset.UtcNow < deadline)
        {
            if (await _channel.BasicGetAsync(queue, autoAck: true, cancellationToken) is { } message)
            {
                messages.Add(message);
            }
            else
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
        }

        return messages;
    }

    /// <summary>
    /// Makes the broker close every connection named <see cref="RabbitMqOptions.ClientName"/>, as a broker restart or a
    /// network failure would, through the management API. Waits until the broker lists them, since its statistics lag.
    /// </summary>
    public async Task CloseClientConnectionsAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{BrokerTestSettings.ManagementPort}/api/") };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{Options.Username}:{Options.Password}")));
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            using var listed = await http.GetAsync(new Uri("connections", UriKind.Relative), cancellationToken);
            listed.EnsureSuccessStatusCode();
            using var json = System.Text.Json.JsonDocument.Parse(await listed.Content.ReadAsStringAsync(cancellationToken));
            var names = json.RootElement.EnumerateArray()
                .Where(c => c.TryGetProperty("client_properties", out var p) && p.TryGetProperty("connection_name", out var n) && n.GetString() == Options.ClientName)
                .Select(static c => c.GetProperty("name").GetString()!)
                .ToList();
            if (names.Count > 0)
            {
                foreach (var name in names)
                {
                    using var closed = await http.DeleteAsync(new Uri($"connections/{Uri.EscapeDataString(name)}", UriKind.Relative), cancellationToken);
                    closed.EnsureSuccessStatusCode();
                }

                return;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("The broker never listed the connection to close.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
    }

    /// <summary>Every message published so far that this test has not read yet.</summary>
    public async Task<IReadOnlyList<BasicGetResult>> DrainAsync(CancellationToken cancellationToken)
    {
        var messages = new List<BasicGetResult>();
        while (await _channel.BasicGetAsync(_queue, autoAck: true, cancellationToken) is { } message)
        {
            messages.Add(message);
        }

        return messages;
    }

    public async ValueTask DisposeAsync()
    {
        // A consumer's queue comes with its dead-letter queue and up to five retry queues.
        foreach (var queue in _queues)
        {
            foreach (var name in new[] { queue, queue + ".dlq" }.Concat(Enumerable.Range(1, 5).Select(n => $"{queue}.retry.{n}")))
            {
                await _channel.QueueDeleteAsync(name);
            }
        }

        await _channel.ExchangeDeleteAsync(Options.Exchange);
        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
