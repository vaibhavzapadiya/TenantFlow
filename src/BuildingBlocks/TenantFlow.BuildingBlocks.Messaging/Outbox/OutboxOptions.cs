using System.ComponentModel.DataAnnotations;

namespace TenantFlow.BuildingBlocks.Messaging.Outbox;

/// <summary>How the outbox publisher works through pending rows (ADR-0021), bound from <c>Messaging:Outbox</c>.</summary>
public sealed class OutboxOptions
{
    public const string SectionName = "Messaging:Outbox";

    /// <summary>The most rows one claim takes, so a large backlog is worked off in bounded steps.</summary>
    [Range(1, 500, ErrorMessage = "Messaging:Outbox:BatchSize must be between 1 and 500.")]
    public int BatchSize { get; set; } = 50;

    /// <summary>How long a claim holds a row. After it passes, another publisher may publish the row again.</summary>
    [Range(typeof(TimeSpan), "00:00:05", "00:10:00", ErrorMessage = "Messaging:Outbox:Lease must be between 5 seconds and 10 minutes.")]
    public TimeSpan Lease { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The pause between polls when the last poll found nothing to publish.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:01:00", ErrorMessage = "Messaging:Outbox:PollInterval must be between 100 milliseconds and 1 minute.")]
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long published rows are kept before they are deleted.</summary>
    [Range(typeof(TimeSpan), "01:00:00", "90.00:00:00", ErrorMessage = "Messaging:Outbox:Retention must be between 1 hour and 90 days.")]
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>The most published rows one cleanup deletes.</summary>
    [Range(1, 10_000, ErrorMessage = "Messaging:Outbox:CleanupBatchSize must be between 1 and 10000.")]
    public int CleanupBatchSize { get; set; } = 500;
}
