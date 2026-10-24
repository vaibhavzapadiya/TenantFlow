using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.BuildingBlocks.Messaging.RabbitMq;

namespace TenantFlow.Worker.Messaging;

/// <summary>
/// Runs one module's consumer, with the usual retries and dead-letter queue. Every message is handled in a DI scope of its
/// own, so nothing from one message, its tenant above all, can reach the next. When the broker is down at startup, it
/// retries until the consumer is running; after that the client's automatic recovery restores it. Registered once per
/// consumer by <see cref="MessagingExtensions"/>.
/// </summary>
internal sealed partial class ConsumerHostedService(
    RabbitMqConnection connection,
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    ILogger<ConsumerHostedService> logger,
    string queue,
    IReadOnlyCollection<string> routingKeys,
    Func<IServiceProvider, IncomingMessage, CancellationToken, Task<MessageOutcome>> handle) : BackgroundService
{
    private static readonly TimeSpan StartRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>Registers a consumer; <paramref name="handle"/> runs in the message's own scope.</summary>
    public static void Add(
        IServiceCollection services,
        string queue,
        IReadOnlyCollection<string> routingKeys,
        Func<IServiceProvider, IncomingMessage, CancellationToken, Task<MessageOutcome>> handle) =>
        // Not AddHostedService: it de-duplicates by implementation type and would keep only the first consumer.
        services.AddSingleton<IHostedService>(provider => new ConsumerHostedService(
            provider.GetRequiredService<RabbitMqConnection>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<ConsumerHostedService>>(),
            queue,
            routingKeys,
            handle));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var consumer = new RabbitMqConsumer(connection, queue, routingKeys, HandleAsync, logger);
        try
        {
            while (true)
            {
                try
                {
                    await consumer.StartAsync(stoppingToken);
                    break;
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    LogStartFailed(logger, exception, consumer.Queue);
                    await Task.Delay(StartRetryDelay, timeProvider, stoppingToken);
                }
            }

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown. An unacknowledged message is redelivered.
        }
    }

    private async Task<MessageOutcome> HandleAsync(IncomingMessage message, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await handle(scope.ServiceProvider, message, cancellationToken);
    }

    [LoggerMessage(EventId = 5103, Level = LogLevel.Warning, Message = "Starting the consumer of {Queue} failed; retrying")]
    private static partial void LogStartFailed(ILogger logger, Exception exception, string queue);
}
