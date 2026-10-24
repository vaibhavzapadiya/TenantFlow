using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TenantFlow.BuildingBlocks.Messaging.Outbox;

/// <summary>
/// Moves outbox rows to the broker (ADR-0021): claim a bounded batch with a lease, publish it and wait for the broker's
/// confirms, then mark the confirmed rows published. A crash between publish and mark leaves the rows claimed until their
/// lease passes, and they are published again with the same message ids: duplicates are expected, loss is not. A row
/// the broker refuses is deferred with a growing backoff, so it never holds back the rows behind it.
/// </summary>
public sealed partial class OutboxPublisher(
    IEnumerable<OutboxStore> stores,
    IMessageBus bus,
    OutboxMetrics metrics,
    TimeProvider clock,
    IOptions<OutboxOptions> options,
    ILogger<OutboxPublisher> logger)
{
    private readonly IReadOnlyList<OutboxStore> _stores = [.. stores];
    private readonly OutboxOptions _options = options.Value;

    /// <summary>
    /// Publishes one batch from every module's outbox. Returns how many messages were confirmed. A module whose outbox
    /// fails is logged and skipped, so it never holds back the modules after it.
    /// </summary>
    public async Task<int> PublishPendingAsync(CancellationToken cancellationToken)
    {
        var total = 0;
        foreach (var store in _stores)
        {
            total += await PerStoreAsync(store, "publish", () => PublishBatchAsync(store, cancellationToken), cancellationToken);
        }

        return total;
    }

    /// <summary>Records each module's backlog for the <see cref="OutboxMetrics"/> gauges.</summary>
    public async Task RefreshBacklogAsync(CancellationToken cancellationToken)
    {
        foreach (var store in _stores)
        {
            await PerStoreAsync(store, "backlog", async () =>
            {
                var (pending, oldest) = await store.GetBacklogAsync(cancellationToken);
                metrics.RecordBacklog(store.Module, pending, oldest);
                return 0;
            }, cancellationToken);
        }
    }

    /// <summary>Deletes one bounded batch of rows published longer ago than the retention, per module.</summary>
    public async Task<int> CleanUpAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow() - _options.Retention;
        var deleted = 0;
        foreach (var store in _stores)
        {
            deleted += await PerStoreAsync(store, "cleanup", () => store.DeletePublishedBeforeAsync(cutoff, _options.CleanupBatchSize, cancellationToken), cancellationToken);
        }

        return deleted;
    }

    private async Task<int> PublishBatchAsync(OutboxStore store, CancellationToken cancellationToken)
    {
        var batch = await store.ClaimAsync(_options.BatchSize, clock.GetUtcNow(), _options.Lease, cancellationToken);
        if (batch.Count == 0)
        {
            return 0;
        }

        var result = await bus.PublishAsync(batch, cancellationToken);

        // Marked only after the confirms; never inside a transaction that spans the broker call.
        if (result.Confirmed.Count > 0)
        {
            await store.MarkPublishedAsync(result.Confirmed, clock.GetUtcNow(), cancellationToken);
            metrics.RecordPublished(store.Module, result.Confirmed.Count);
        }

        foreach (var failure in result.Failed)
        {
            await store.DeferAsync(failure.MessageId, $"{failure.Error.GetType().Name}: {failure.Error.Message}", clock.GetUtcNow(), cancellationToken);
        }

        if (result.Failed.Count > 0)
        {
            LogPublishFailed(logger, result.Failed[0].Error, store.Module, result.Failed.Count);
        }

        return result.Confirmed.Count;
    }

    private async Task<int> PerStoreAsync(OutboxStore store, string operation, Func<Task<int>> work, CancellationToken cancellationToken)
    {
        try
        {
            return await work();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogStoreFailed(logger, exception, operation, store.Module);
            return 0;
        }
    }

    [LoggerMessage(EventId = 5003, Level = LogLevel.Error, Message = "The {Operation} step failed for the {Module} outbox; the other modules carry on")]
    private static partial void LogStoreFailed(ILogger logger, Exception exception, string operation, string module);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Warning, Message = "Publishing the {Module} outbox failed for {Count} message(s); they stay pending and are retried with backoff")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception, string module, int count);
}
