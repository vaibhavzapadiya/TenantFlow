using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TenantFlow.BuildingBlocks.Observability;

/// <summary>
/// Publishes the host's health checks as the gauge <c>tenantflow.health.status</c> (SPEC-0010 criteria 11 and 14): 1 when a
/// check is healthy or degraded, 0 when it is unhealthy, per check name (<c>postgresql</c>, <c>rabbitmq</c>: fixed in code).
/// The dependency alerts (database or broker down) read it, and inhibit the symptom alerts while they fire (ADR-0029). The
/// checks run every <see cref="Period"/> through ASP.NET Core's health check publisher, not on the request path.
/// </summary>
internal sealed class HealthStatusPublisher : IHealthCheckPublisher, IDisposable
{
    public const string MeterName = "TenantFlow.Health";
    public const string StatusGauge = "tenantflow.health.status";
    public const string CheckTag = "check";

    /// <summary>How often the checks run for the gauge.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(15);

    private readonly Meter _meter = new(MeterName);
    private readonly ConcurrentDictionary<string, long> _status = new(StringComparer.Ordinal);

    public HealthStatusPublisher() =>
        _meter.CreateObservableGauge(
            StatusGauge,
            () => _status.Select(static s => new Measurement<long>(s.Value, new KeyValuePair<string, object?>(CheckTag, s.Key))),
            "{status}",
            "Health check results: 1 healthy or degraded, 0 unhealthy, per check.");

    public Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        foreach (var (name, entry) in report.Entries)
        {
            _status[name] = entry.Status == HealthStatus.Unhealthy ? 0 : 1;
        }

        return Task.CompletedTask;
    }

    public void Dispose() => _meter.Dispose();
}
