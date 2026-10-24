using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace TenantFlow.Testing;

/// <summary>
/// Captures finished spans and log records from a host's real OpenTelemetry pipeline. Values are
/// copied when each item ends, because OpenTelemetry reuses log record objects after export.
/// </summary>
public sealed class TelemetryCapture : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<CapturedSpan> _spans = [];
    private readonly List<CapturedLog> _logs = [];
    private readonly SemaphoreSlim _changed = new(0);
    private bool _disposed;

    /// <summary>A closed local port: exporters fail fast and nothing reaches a running collector.</summary>
    public const string UnreachableOtlpEndpoint = "http://127.0.0.1:1";

    /// <summary>Export timeout for test hosts, in milliseconds (the OpenTelemetry default is 10 000).</summary>
    public const string ShortExportTimeoutMilliseconds = "500";

    /// <summary>
    /// Host settings every test host applies so telemetry export never reaches a collector a developer
    /// has running locally, and host shutdown stays fast when the exports to the closed port fail.
    /// </summary>
    public static IReadOnlyDictionary<string, string> TestHostSettings { get; } = new Dictionary<string, string>
    {
        ["OTEL_EXPORTER_OTLP_ENDPOINT"] = UnreachableOtlpEndpoint,
        ["OTEL_EXPORTER_OTLP_TIMEOUT"] = ShortExportTimeoutMilliseconds,
    };

    /// <summary>Adds capture processors to the tracer and logger providers the host builds.</summary>
    public void AttachTo(IServiceCollection services)
    {
        services.ConfigureOpenTelemetryTracerProvider(builder => builder.AddProcessor(new SpanProcessor(this)));
        services.ConfigureOpenTelemetryLoggerProvider(builder => builder.AddProcessor(new LogProcessor(this)));
    }

    /// <summary>Every span captured so far.</summary>
    public IReadOnlyList<CapturedSpan> Spans
    {
        get
        {
            lock (_gate)
            {
                return [.. _spans];
            }
        }
    }

    /// <summary>Waits until an item matching <paramref name="predicate"/> has been captured.</summary>
    public Task<CapturedSpan> WaitForSpanAsync(Func<CapturedSpan, bool> predicate, CancellationToken cancellationToken) =>
        WaitForAsync(_spans, predicate, cancellationToken);

    /// <inheritdoc cref="WaitForSpanAsync"/>
    public Task<CapturedLog> WaitForLogAsync(Func<CapturedLog, bool> predicate, CancellationToken cancellationToken) =>
        WaitForAsync(_logs, predicate, cancellationToken);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _changed.Dispose();
    }

    private async Task<T> WaitForAsync<T>(List<T> items, Func<T, bool> predicate, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            lock (_gate)
            {
                foreach (var item in items)
                {
                    if (predicate(item))
                    {
                        return item;
                    }
                }
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || !await _changed.WaitAsync(remaining, cancellationToken))
            {
                string captured;
                lock (_gate)
                {
                    captured = string.Join(Environment.NewLine, items);
                }

                throw new TimeoutException(
                    $"No matching {typeof(T).Name} was captured within 10 seconds. Captured:{Environment.NewLine}{captured}");
            }
        }
    }

    // An item that ends after the test disposed the capture (a host still finishing a request) is dropped: telemetry
    // capture must never fail the request it observes.
    private void Add<T>(List<T> items, T item)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            items.Add(item);
            _changed.Release();
        }
    }

    private static Dictionary<string, object> ResourceOf(BaseProvider? provider) =>
        provider?.GetResource().Attributes.ToDictionary(pair => pair.Key, pair => pair.Value) ?? [];

    private sealed class SpanProcessor(TelemetryCapture capture) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data) => capture.Add(capture._spans, new CapturedSpan(
            data.OperationName, data.DisplayName, data.Kind, data.TraceId, data.SpanId,
            data.TagObjects.ToDictionary(tag => tag.Key, tag => tag.Value?.ToString()),
            ResourceOf(ParentProvider)));
    }

    private sealed class LogProcessor(TelemetryCapture capture) : BaseProcessor<LogRecord>
    {
        public override void OnEnd(LogRecord data)
        {
            var attributes = (data.Attributes ?? []).ToDictionary(pair => pair.Key, pair => pair.Value?.ToString(), StringComparer.Ordinal);
            var exported = attributes.Select(pair => $"{pair.Key}={pair.Value}").ToList();
            data.ForEachScope(static (scope, items) =>
            {
                foreach (var (key, value) in scope)
                {
                    items.Add($"{key}={value}");
                }
            }, exported);
            if (data.Exception is { } exception)
            {
                exported.Add(exception.ToString());
            }

            capture.Add(capture._logs, new CapturedLog(
                data.CategoryName, data.FormattedMessage ?? data.Body, data.TraceId, data.SpanId, ResourceOf(ParentProvider))
            {
                Exported = exported,
                Attributes = attributes,
            });
        }
    }
}
