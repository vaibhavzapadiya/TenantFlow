using System.Diagnostics.Metrics;
using TenantFlow.ImportsExports.Application.Observability;

namespace TenantFlow.ImportsExports.Tests;

/// <summary>SPEC-0008 criterion 11: a Worker instance reports its due-job snapshot only while it is fresh.</summary>
public sealed class ImportsExportsMetricsTests
{
    [Fact]
    public void A_snapshot_is_reported_while_fresh_and_dropped_once_another_instance_would_have_taken_the_next()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
        using var metrics = new ImportsExportsMetrics(clock);
        metrics.RecordDue([("imports_exports.run_import", 3, clock.Now - TimeSpan.FromSeconds(90))], clock.Now);

        var fresh = Observe(ImportsExportsMetrics.DueJobsGauge);
        var age = Observe(ImportsExportsMetrics.OldestDueAgeGauge);
        clock.Now += ImportsExportsMetrics.SnapshotLifetime + TimeSpan.FromSeconds(1);
        var stale = Observe(ImportsExportsMetrics.DueJobsGauge);

        Assert.Equal([3L], fresh);
        Assert.Equal([90L], age);
        Assert.Empty(stale);
    }

    // Reads every gauge of that name in the process once, as a metrics export would.
    private static List<long> Observe(string instrumentName)
    {
        var values = new List<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == instrumentName && instrument.Meter.Name == ImportsExportsMetrics.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => values.Add(value));
        listener.Start();
        listener.RecordObservableInstruments();
        return values;
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
