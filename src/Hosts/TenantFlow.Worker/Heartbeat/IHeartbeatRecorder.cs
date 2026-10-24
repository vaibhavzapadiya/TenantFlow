namespace TenantFlow.Worker.Heartbeat;

/// <summary>Records one heartbeat. Separate from the loop so tests can simulate a failing tick.</summary>
internal interface IHeartbeatRecorder
{
    void Record(long sequence);
}
