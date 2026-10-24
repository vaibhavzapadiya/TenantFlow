using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TenantFlow.ImportsExports.Application.Observability;

/// <summary>
/// Import and export metrics (SPEC-0008 criterion 11). The due-job gauges report the last snapshot the
/// <c>imports_exports.metrics</c> task took from the due rows (kinds, counts and due times only; no tenant data), and only
/// while it is younger than <see cref="SnapshotLifetime"/>: Quartz runs the task on one Worker instance per fire, so another
/// instance's old snapshot is dropped rather than reported, and a sum across instances counts each job once. The age is
/// computed when the gauge is read. The runners record each job that ends.
/// Labels are fixed names, never tenant or job ids.
/// </summary>
public sealed class ImportsExportsMetrics : IDisposable
{
    public const string MeterName = "TenantFlow.ImportsExports";
    public const string DueJobsGauge = "tenantflow.imports_exports.due_jobs";
    public const string OldestDueAgeGauge = "tenantflow.imports_exports.oldest_due_age";
    public const string JobsCounter = "tenantflow.imports_exports.jobs";
    public const string FailedRowsCounter = "tenantflow.imports_exports.failed_rows";
    public const string JobDurationHistogram = "tenantflow.imports_exports.job_duration";
    public const string KindTag = "kind";
    public const string OutcomeTag = "outcome";

    public const string ImportJob = "import";
    public const string ExportJob = "export";

    /// <summary>How long a snapshot is reported: four fires of the 30-second task.</summary>
    public static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(2);

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _jobs;
    private readonly Counter<long> _failedRows;
    private readonly Histogram<double> _duration;
    private readonly TimeProvider _clock;
    private volatile Snapshot _due = new(new Dictionary<string, (long, DateTimeOffset)>(), DateTimeOffset.MinValue);

    public ImportsExportsMetrics(TimeProvider clock)
    {
        _clock = clock;
        _meter.CreateObservableGauge(DueJobsGauge, () => Fresh().Select(static d => new Measurement<long>(d.Value.Due, Kind(d.Key))), "{job}", "Due rows of imports and exports waiting to run, per kind.");
        _meter.CreateObservableGauge(OldestDueAgeGauge, ObserveAge, "s", "How long the oldest due row of each kind has waited.");
        _jobs = _meter.CreateCounter<long>(JobsCounter, "{job}", "Import and export jobs that ended, by kind and outcome.");
        _failedRows = _meter.CreateCounter<long>(FailedRowsCounter, "{row}", "Import rows that were not imported.");
        _duration = _meter.CreateHistogram<double>(JobDurationHistogram, "s", "Time from submitting a job to its end.");
    }

    /// <summary>Replaces the due-row snapshot taken at <paramref name="takenAt"/>: per kind, the rows due and the oldest due time.</summary>
    public void RecordDue(IEnumerable<(string Kind, long Due, DateTimeOffset Oldest)> snapshot, DateTimeOffset takenAt) =>
        _due = new(snapshot.ToDictionary(static d => d.Kind, static d => (d.Due, d.Oldest), StringComparer.Ordinal), takenAt);

    /// <summary>Records a job that ended (<paramref name="kind"/> is <see cref="ImportJob"/> or <see cref="ExportJob"/>).</summary>
    public void RecordFinished(string kind, string outcome, TimeSpan duration, int failedRows = 0)
    {
        var tags = new TagList { { KindTag, kind }, { OutcomeTag, outcome } };
        _jobs.Add(1, tags);
        _duration.Record(Math.Max(0, duration.TotalSeconds), tags);
        if (failedRows > 0)
        {
            _failedRows.Add(failedRows, new KeyValuePair<string, object?>(KindTag, kind));
        }
    }

    public void Dispose() => _meter.Dispose();

    private static KeyValuePair<string, object?> Kind(string kind) => new(KindTag, kind);

    private IEnumerable<Measurement<long>> ObserveAge()
    {
        var now = _clock.GetUtcNow();
        return Fresh().Select(d => new Measurement<long>(Math.Max(0, (long)(now - d.Value.Oldest).TotalSeconds), Kind(d.Key)));
    }

    private IReadOnlyDictionary<string, (long Due, DateTimeOffset Oldest)> Fresh()
    {
        var snapshot = _due;
        return _clock.GetUtcNow() - snapshot.TakenAt <= SnapshotLifetime ? snapshot.Kinds : new Dictionary<string, (long, DateTimeOffset)>();
    }

    private sealed record Snapshot(IReadOnlyDictionary<string, (long Due, DateTimeOffset Oldest)> Kinds, DateTimeOffset TakenAt);
}
