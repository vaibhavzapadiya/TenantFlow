using System.Diagnostics.Metrics;

namespace TenantFlow.Worker.Heartbeat;

/// <summary>
/// Emits one span, one log line, and one counter increment per heartbeat. The log line is written
/// inside the span, so it carries the span's trace ID once telemetry export is configured (S4).
/// </summary>
internal sealed partial class HeartbeatRecorder : IHeartbeatRecorder
{
    private readonly Counter<long> _heartbeats;
    private readonly ILogger<HeartbeatRecorder> _logger;

    public HeartbeatRecorder(IMeterFactory meterFactory, ILogger<HeartbeatRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _logger = logger;
        _heartbeats = meterFactory.Create(WorkerTelemetry.MeterName).CreateCounter<long>(
            WorkerTelemetry.HeartbeatCounter, unit: "{heartbeat}", description: "Heartbeats recorded by the worker.");
    }

    public void Record(long sequence)
    {
        using var activity = WorkerTelemetry.ActivitySource.StartActivity(WorkerTelemetry.HeartbeatActivity);
        activity?.SetTag("tenantflow.heartbeat.sequence", sequence);
        LogHeartbeat(_logger, sequence);
        _heartbeats.Add(1);
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Worker heartbeat {Sequence}")]
    private static partial void LogHeartbeat(ILogger logger, long sequence);
}
