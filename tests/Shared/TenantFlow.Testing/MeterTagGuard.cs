using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics.Metrics;

namespace TenantFlow.Testing;

/// <summary>
/// SPEC-0010 criterion 5 (threat model 0008 DP-A05): watches every measurement of every <c>TenantFlow.*</c> meter in the
/// process and records a violation when a tag key is outside <see cref="AllowedTagKeys"/> or a tag value parses as a
/// GUID (a tenant, user, order or payment id). Test assemblies that record TenantFlow metrics declare
/// <see cref="MeterTagGuardFixture"/> as an assembly fixture, so every instrument any of their tests drives is checked.
/// </summary>
public sealed class MeterTagGuard : IDisposable
{
    /// <summary>
    /// The metric label keys of the telemetry contract (SPEC-0010 Contract). Each has a closed value list, documented
    /// where the instrument is defined. Adding a key is a contract change: add it here in the same change.
    /// </summary>
    public static readonly FrozenSet<string> AllowedTagKeys = FrozenSet.Create(
        StringComparer.Ordinal,
        "scheme", "reason", "module", "outcome", "kind", "consumer", "job", "provider", "operation", "check");

    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<string, byte> _violations = new(StringComparer.Ordinal);
    private readonly Timer _observer;

    /// <param name="meterPrefix">The meters to watch; the guard's own tests use a prefix of their own.</param>
    public MeterTagGuard(string meterPrefix = "TenantFlow.")
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name.StartsWith(meterPrefix, StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Check(instrument, tags));
        _listener.SetMeasurementEventCallback<int>((instrument, _, tags, _) => Check(instrument, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => Check(instrument, tags));
        _listener.Start();
        // Gauges report only when read, as an export would read them.
        _observer = new Timer(static state => ((MeterListener)state!).RecordObservableInstruments(), _listener, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    /// <summary>One line per distinct violation: instrument, tag key, and why.</summary>
    public IReadOnlyList<string> Violations
    {
        get
        {
            _listener.RecordObservableInstruments();
            return [.. _violations.Keys.Order(StringComparer.Ordinal)];
        }
    }

    public void Dispose()
    {
        _observer.Dispose();
        _listener.Dispose();
    }

    private void Check(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var (key, value) in tags)
        {
            if (!AllowedTagKeys.Contains(key))
            {
                _violations.TryAdd($"{instrument.Name}: tag key '{key}' is not in the allow-list", 0);
            }

            if (value is Guid || (value is string text && Guid.TryParse(text, out _)))
            {
                _violations.TryAdd($"{instrument.Name}: tag '{key}' carries a GUID value", 0);
            }
        }
    }
}

/// <summary>
/// Assembly fixture around <see cref="MeterTagGuard"/>: when the assembly's tests end, any violation fails the run
/// (as an assembly cleanup failure that names each instrument and tag).
/// </summary>
public sealed class MeterTagGuardFixture : IDisposable
{
    private readonly MeterTagGuard _guard = new();

    public void Dispose()
    {
        var violations = _guard.Violations;
        _guard.Dispose();
        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                "TenantFlow metrics broke the label rules (SPEC-0010 criterion 5):" + Environment.NewLine + string.Join(Environment.NewLine, violations));
        }
    }
}
