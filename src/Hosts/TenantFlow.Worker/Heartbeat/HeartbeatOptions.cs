using System.ComponentModel.DataAnnotations;

namespace TenantFlow.Worker.Heartbeat;

/// <summary>Heartbeat settings, bound from the <c>Worker</c> configuration section.</summary>
internal sealed class HeartbeatOptions
{
    public const string SectionName = "Worker";

    [Range(typeof(TimeSpan), "00:00:01", "01:00:00",
        ErrorMessage = "Worker:HeartbeatInterval must be between 1 second and 1 hour.")]
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);
}
