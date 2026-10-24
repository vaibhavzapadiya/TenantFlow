using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TenantFlow.Worker.Heartbeat;

namespace TenantFlow.Worker.Tests;

/// <summary>
/// SPEC-0001 criterion 11: one heartbeat produces exactly one span, one log line, and one
/// increment of <c>tenantflow.worker.heartbeats</c>.
/// </summary>
public sealed class HeartbeatRecorderTests
{
    private const long UniqueSequence = 987_654_321;

    [Fact]
    public void One_heartbeat_emits_one_span_one_log_line_and_one_counter_increment()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = services.GetRequiredService<IMeterFactory>();
        var logger = new FakeLogger<HeartbeatRecorder>();

        var spans = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "TenantFlow.Worker",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            // The listener is process-wide; keep only this test's heartbeat (a sequence no heartbeat loop reaches), not a
            // heartbeat from a worker host started by another test.
            ActivityStopped = activity =>
            {
                if (activity.GetTagItem("tenantflow.heartbeat.sequence") is UniqueSequence)
                {
                    lock (spans)
                    {
                        spans.Add(activity);
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(activityListener);

        var increments = new List<long>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            // Only this test's meter: other hosts in the test run create meters with the same name.
            if (ReferenceEquals(instrument.Meter.Scope, meterFactory) && instrument.Name == "tenantflow.worker.heartbeats")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, value, _, _) => increments.Add(value));
        meterListener.Start();

        var recorder = new HeartbeatRecorder(meterFactory, logger);
        recorder.Record(UniqueSequence);

        var span = Assert.Single(spans);
        Assert.Equal("worker.heartbeat", span.OperationName);
        var log = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Information, log.Level);
        Assert.Contains("987654321", log.Message, StringComparison.Ordinal);
        Assert.Equal([1L], increments);
    }
}
