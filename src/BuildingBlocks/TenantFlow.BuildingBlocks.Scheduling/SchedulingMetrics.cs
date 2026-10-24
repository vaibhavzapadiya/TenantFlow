using System.Diagnostics;
using System.Diagnostics.Metrics;
using Quartz;

namespace TenantFlow.BuildingBlocks.Scheduling;

/// <summary>
/// Scheduled-job metrics (SPEC-0010 criterion 3): every fire of a <see cref="ScheduledTaskJob"/> or a
/// <see cref="WorkflowTimerJob"/> is counted by job and outcome, with its duration and its lateness (the actual fire time
/// minus the scheduled one; a fire missed while no Worker ran is made late, once). The <c>job</c> label is a task name or
/// <c>timers.&lt;schema&gt;</c>, both fixed in code; outcomes are <see cref="Succeeded"/> and <see cref="Failed"/>.
/// </summary>
public sealed class SchedulingMetrics : IDisposable
{
    public const string MeterName = "TenantFlow.Scheduling";
    public const string RunsCounter = "tenantflow.scheduling.runs";
    public const string DurationHistogram = "tenantflow.scheduling.duration";
    public const string LatenessHistogram = "tenantflow.scheduling.lateness";
    public const string JobTag = "job";
    public const string OutcomeTag = "outcome";

    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _runs;
    private readonly Histogram<double> _duration;
    private readonly Histogram<double> _lateness;

    public SchedulingMetrics()
    {
        _runs = _meter.CreateCounter<long>(RunsCounter, "{run}", "Scheduled job fires that ended, by job and outcome.");
        _duration = _meter.CreateHistogram<double>(DurationHistogram, "s", "How long a scheduled job fire ran.");
        _lateness = _meter.CreateHistogram<double>(LatenessHistogram, "s", "How long after its scheduled time a job fire started.");
    }

    /// <summary>The job label of a module's timer job.</summary>
    public static string TimerJob(string schema) => "timers." + schema;

    /// <summary>Records one fire that ended. Lateness comes from Quartz; duration from <paramref name="startedAt"/>.</summary>
    public void RecordRun(IJobExecutionContext context, string job, bool succeeded, long startedAt)
    {
        ArgumentNullException.ThrowIfNull(context);
        var tags = new TagList { { JobTag, job }, { OutcomeTag, succeeded ? Succeeded : Failed } };
        _runs.Add(1, tags);
        _duration.Record(Stopwatch.GetElapsedTime(startedAt).TotalSeconds, new KeyValuePair<string, object?>(JobTag, job));
        if (context.ScheduledFireTimeUtc is { } scheduled)
        {
            _lateness.Record(Math.Max(0, (context.FireTimeUtc - scheduled).TotalSeconds), new KeyValuePair<string, object?>(JobTag, job));
        }
    }

    public void Dispose() => _meter.Dispose();
}
