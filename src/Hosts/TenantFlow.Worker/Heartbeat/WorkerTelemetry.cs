using System.Diagnostics;

namespace TenantFlow.Worker.Heartbeat;

/// <summary>Telemetry names owned by the worker (SPEC-0001 contract).</summary>
internal static class WorkerTelemetry
{
    public const string SourceName = "TenantFlow.Worker";
    public const string MeterName = "TenantFlow.Worker";
    public const string HeartbeatActivity = "worker.heartbeat";
    public const string HeartbeatCounter = "tenantflow.worker.heartbeats";

    public static readonly ActivitySource ActivitySource = new(SourceName);
}
