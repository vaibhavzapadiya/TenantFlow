using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace TenantFlow.BuildingBlocks.Messaging.Outbox;

/// <summary>
/// Outbox backlog metrics per module (SPEC-0005 criterion 8). The publisher records the pending count and the oldest
/// pending time on each refresh; the gauges report them without querying the database. The age is computed when the gauge
/// is read, so it keeps rising while refreshes fail (database down), exactly when it matters.
/// </summary>
public sealed class OutboxMetrics : IDisposable
{
    public const string MeterName = "TenantFlow.Messaging";
    public const string PendingGauge = "tenantflow.outbox.pending";
    public const string OldestPendingAgeGauge = "tenantflow.outbox.oldest_pending_age";
    public const string PublishedCounter = "tenantflow.outbox.published";
    public const string ModuleTag = "module";

    private readonly Meter _meter = new(MeterName);
    private readonly ConcurrentDictionary<string, (long Pending, DateTimeOffset? Oldest)> _snapshots = new(StringComparer.Ordinal);
    private readonly Counter<long> _published;
    private readonly TimeProvider _clock;

    public OutboxMetrics(TimeProvider clock)
    {
        _clock = clock;
        _meter.CreateObservableGauge(PendingGauge, () => Observe(static (s, _) => s.Pending), "{message}", "Outbox rows waiting to be published.");
        _meter.CreateObservableGauge(OldestPendingAgeGauge, () => Observe(static (s, now) => s.Oldest is { } oldest ? Math.Max(0, (long)(now - oldest).TotalSeconds) : 0), "s", "Age of the oldest outbox row waiting to be published.");
        _published = _meter.CreateCounter<long>(PublishedCounter, "{message}", "Outbox rows published and confirmed by the broker.");
    }

    public void RecordBacklog(string module, long pending, DateTimeOffset? oldest) => _snapshots[module] = (pending, oldest);

    public void RecordPublished(string module, int count) => _published.Add(count, new KeyValuePair<string, object?>(ModuleTag, module));

    public void Dispose() => _meter.Dispose();

    private IEnumerable<Measurement<long>> Observe(Func<(long Pending, DateTimeOffset? Oldest), DateTimeOffset, long> value)
    {
        var now = _clock.GetUtcNow();
        return _snapshots.Select(s => new Measurement<long>(value(s.Value, now), new KeyValuePair<string, object?>(ModuleTag, s.Key)));
    }
}
