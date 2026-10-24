using System.Diagnostics;
using System.Diagnostics.Metrics;
using TenantFlow.Payments.Domain.Payments;

namespace TenantFlow.Payments.Application.Observability;

/// <summary>
/// Payment and refund metrics (SPEC-0010 criterion 4, threat model 0003 PY-A25). Outcomes are counted after the change
/// commits: each payment or refund that moves to a new status counts once, under that status's name. A refund a provider
/// reports failed after it succeeded counts as <see cref="Reversed"/>. The unresolved gauges report the last snapshot of
/// the <c>payments.metrics</c> task while it is younger than <see cref="SnapshotLifetime"/>, so a failing task leaves a
/// gap, never a stale value. Labels are fixed names, never a tenant, payment or provider reference.
/// </summary>
public sealed class PaymentsMetrics : IDisposable
{
    public const string MeterName = "TenantFlow.Payments";
    public const string AttemptsCounter = "tenantflow.payments.attempts";
    public const string RefundsCounter = "tenantflow.refunds.completed";
    public const string UnresolvedGauge = "tenantflow.payments.unresolved";
    public const string OldestUnresolvedAgeGauge = "tenantflow.payments.oldest_unresolved_age";
    public const string ProviderDurationHistogram = "tenantflow.payments.provider_duration";
    public const string OutcomeTag = "outcome";
    public const string KindTag = "kind";
    public const string ProviderTag = "provider";
    public const string OperationTag = "operation";

    /// <summary>The refund outcome of a success the provider took back.</summary>
    public const string Reversed = "reversed";

    // Unresolved kinds.
    public const string PaymentKind = "payment";
    public const string RefundKind = "refund";

    // Provider call outcomes.
    public const string Answered = "answered";
    public const string Unavailable = "unavailable";
    public const string Error = "error";

    /// <summary>Four fires of the 30-second task.</summary>
    public static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(2);

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _attempts;
    private readonly Counter<long> _refunds;
    private readonly Histogram<double> _providerDuration;
    private readonly TimeProvider _clock;
    private volatile Snapshot _unresolved = new(new Dictionary<string, (long, DateTimeOffset)>(), DateTimeOffset.MinValue);

    public PaymentsMetrics(TimeProvider clock)
    {
        _clock = clock;
        _attempts = _meter.CreateCounter<long>(AttemptsCounter, "{payment}", "Payments that moved to a new status, by that status.");
        _refunds = _meter.CreateCounter<long>(RefundsCounter, "{refund}", "Refunds that moved to a new status, by that status, and refunds reversed after success.");
        _providerDuration = _meter.CreateHistogram<double>(ProviderDurationHistogram, "s", "Payment provider calls, by provider, operation and outcome.");
        _meter.CreateObservableGauge(UnresolvedGauge, () => Fresh().Select(static u => new Measurement<long>(u.Value.Count, Kind(u.Key))), "{payment}", "Payments and refunds checked at least once by reconciliation and still without a final outcome.");
        _meter.CreateObservableGauge(OldestUnresolvedAgeGauge, ObserveAge, "s", "Age of the oldest unresolved payment or refund, per kind.");
    }

    public void RecordPayment(PaymentStatus status) => _attempts.Add(1, new KeyValuePair<string, object?>(OutcomeTag, status.ToName()));

    public void RecordRefund(PaymentStatus status) => _refunds.Add(1, new KeyValuePair<string, object?>(OutcomeTag, status.ToName()));

    public void RecordRefundReversed() => _refunds.Add(1, new KeyValuePair<string, object?>(OutcomeTag, Reversed));

    public void RecordProviderCall(string provider, string operation, string outcome, long startedAt) =>
        _providerDuration.Record(
            Stopwatch.GetElapsedTime(startedAt).TotalSeconds,
            new TagList { { ProviderTag, provider }, { OperationTag, operation }, { OutcomeTag, outcome } });

    /// <summary>Replaces the unresolved snapshot taken at <paramref name="takenAt"/>: per kind, the count and the oldest creation time.</summary>
    public void RecordUnresolved(IEnumerable<(string Kind, long Count, DateTimeOffset Oldest)> snapshot, DateTimeOffset takenAt) =>
        _unresolved = new(snapshot.ToDictionary(static u => u.Kind, static u => (u.Count, u.Oldest), StringComparer.Ordinal), takenAt);

    public void Dispose() => _meter.Dispose();

    private static KeyValuePair<string, object?> Kind(string kind) => new(KindTag, kind);

    private IEnumerable<Measurement<long>> ObserveAge()
    {
        var now = _clock.GetUtcNow();
        return Fresh().Select(u => new Measurement<long>(u.Value.Count == 0 ? 0 : Math.Max(0, (long)(now - u.Value.Oldest).TotalSeconds), Kind(u.Key)));
    }

    private IReadOnlyDictionary<string, (long Count, DateTimeOffset Oldest)> Fresh()
    {
        var snapshot = _unresolved;
        return _clock.GetUtcNow() - snapshot.TakenAt <= SnapshotLifetime ? snapshot.Kinds : new Dictionary<string, (long, DateTimeOffset)>();
    }

    private sealed record Snapshot(IReadOnlyDictionary<string, (long Count, DateTimeOffset Oldest)> Kinds, DateTimeOffset TakenAt);
}
