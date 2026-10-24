using Microsoft.Extensions.Options;
using TenantFlow.BuildingBlocks.Messaging.Outbox;

namespace TenantFlow.Worker.Messaging;

/// <summary>
/// Runs the outbox publisher (ADR-0021): publishes batch after batch while rows are pending, then polls every
/// <see cref="OutboxOptions.PollInterval"/>. The backlog metrics are refreshed every few seconds (their age gauge keeps
/// rising between refreshes); published rows past the retention are deleted every few minutes. A failed poll is logged
/// and retried on the next one, so a broker or database outage delays messages without stopping the worker.
/// </summary>
internal sealed partial class OutboxPublisherService(
    OutboxPublisher publisher,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxPublisherService> logger) : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BacklogInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextCleanup = timeProvider.GetUtcNow();
        var nextBacklog = timeProvider.GetUtcNow();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var published = 0;
                try
                {
                    published = await publisher.PublishPendingAsync(stoppingToken);
                    if (timeProvider.GetUtcNow() >= nextBacklog)
                    {
                        // Counting a large backlog after every batch would cost more than the batch; a few seconds is fresh enough.
                        await publisher.RefreshBacklogAsync(stoppingToken);
                        nextBacklog = timeProvider.GetUtcNow() + BacklogInterval;
                    }

                    if (timeProvider.GetUtcNow() >= nextCleanup)
                    {
                        await publisher.CleanUpAsync(stoppingToken);
                        nextCleanup = timeProvider.GetUtcNow() + CleanupInterval;
                    }
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    LogPollFailed(logger, exception);
                }

                // A full batch means more may be waiting: continue at once. Otherwise wait for new rows.
                if (published == 0)
                {
                    await Task.Delay(options.Value.PollInterval, timeProvider, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown. A claimed row is released by its lease.
        }
    }

    [LoggerMessage(EventId = 5002, Level = LogLevel.Error, Message = "Outbox poll failed; retrying on the next poll")]
    private static partial void LogPollFailed(ILogger logger, Exception exception);
}
