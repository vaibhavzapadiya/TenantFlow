using TenantFlow.BuildingBlocks.Persistence.Retention;

namespace TenantFlow.Notifications.Infrastructure;

/// <summary>
/// The Notifications module's retention rules (SPEC-0009 criteria 8, ADR-0025 decision 3). The Worker's retention task runs them
/// tenant by tenant; see <c>docs/security/privacy-and-retention.md</c>.
/// </summary>
public static class NotificationsRetention
{
    public static readonly TimeSpan ProcessedMessages = TimeSpan.FromDays(30);

    public static IReadOnlyList<RetentionRule> Rules { get; } =
    [
        RetentionRule.Delete("notifications.processed_messages", "notifications.processed_messages", "processed_at < @cutoff", ProcessedMessages),
    ];
}
