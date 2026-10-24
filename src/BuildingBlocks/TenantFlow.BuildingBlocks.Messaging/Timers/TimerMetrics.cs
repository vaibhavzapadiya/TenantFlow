using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace TenantFlow.BuildingBlocks.Messaging.Timers;

/// <summary>
/// The dead-timer gauge (SPEC-0010 criterion 3, threat model 0005 MW-A07): per module, the timers that failed
/// <see cref="WorkflowTimerRunner.MaxFailures"/> times in a row and are never claimed again until an operator clears them.
/// Each module's timer job records the count after its run; a count older than <see cref="SnapshotLifetime"/> is not
/// reported, so a failing count query leaves a gap (and the job's failure), never a stale "healthy" value.
/// </summary>
public sealed class TimerMetrics : IDisposable
{
    public const string DeadTimersGauge = "tenantflow.messaging.dead_timers";
    public const string ModuleTag = "module";

    /// <summary>Three fires of the one-minute timer job.</summary>
    public static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(3);

    private readonly Meter _meter = new(Outbox.OutboxMetrics.MeterName);
    private readonly ConcurrentDictionary<string, (long Dead, DateTimeOffset TakenAt)> _dead = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public TimerMetrics(TimeProvider clock)
    {
        _clock = clock;
        _meter.CreateObservableGauge(DeadTimersGauge, Observe, "{timer}", "Workflow timers that are dead and wait for an operator, per module.");
    }

    public void RecordDead(string module, long dead) => _dead[module] = (dead, _clock.GetUtcNow());

    public void Dispose() => _meter.Dispose();

    private IEnumerable<Measurement<long>> Observe()
    {
        var now = _clock.GetUtcNow();
        return _dead
            .Where(d => now - d.Value.TakenAt <= SnapshotLifetime)
            .Select(static d => new Measurement<long>(d.Value.Dead, new KeyValuePair<string, object?>(ModuleTag, d.Key)));
    }
}
