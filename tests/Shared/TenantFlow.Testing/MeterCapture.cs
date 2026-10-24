using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace TenantFlow.Testing;

/// <summary>
/// Captures the measurements of one counter, gauge or histogram by name, from any meter in the process, with their labels. The listener is
/// process-wide, so tests that use it run sequentially (see <c>.agents/rules/tests.md</c>) and read only what happened
/// after <see cref="Clear"/>.
/// </summary>
public sealed class MeterCapture : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<CapturedMeasurement> _measurements = new();

    public MeterCapture(string instrumentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instrumentName);
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == instrumentName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            _measurements.Enqueue(new CapturedMeasurement(value, tags.ToArray().ToDictionary(static tag => tag.Key, static tag => tag.Value?.ToString()))));
        // Histograms in seconds are doubles: captured rounded, which is enough to see that and how they were recorded.
        _listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
            _measurements.Enqueue(new CapturedMeasurement((long)Math.Round(value), tags.ToArray().ToDictionary(static tag => tag.Key, static tag => tag.Value?.ToString()))));
        _listener.Start();
    }

    public IReadOnlyList<CapturedMeasurement> Measurements => [.. _measurements];

    public void Clear() => _measurements.Clear();

    /// <summary>Asks every observable instrument (gauges) for its current value, as a metrics export would.</summary>
    public void RecordObservableInstruments() => _listener.RecordObservableInstruments();

    public void Dispose() => _listener.Dispose();
}

public sealed record CapturedMeasurement(long Value, IReadOnlyDictionary<string, string?> Tags);
