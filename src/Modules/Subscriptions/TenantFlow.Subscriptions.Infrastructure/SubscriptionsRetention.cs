using TenantFlow.BuildingBlocks.Persistence.Retention;

namespace TenantFlow.Subscriptions.Infrastructure;

/// <summary>
/// The Subscriptions module's retention rules (SPEC-0009 criteria 8, ADR-0025 decision 3). The Worker's retention task runs them
/// tenant by tenant; see <c>docs/security/privacy-and-retention.md</c>.
/// </summary>
public static class SubscriptionsRetention
{
    public static readonly TimeSpan CompletedTimers = TimeSpan.FromDays(30);

    public static readonly TimeSpan DeadTimers = TimeSpan.FromDays(90);

    public static IReadOnlyList<RetentionRule> Rules { get; } =
    [
        RetentionRule.Delete("subscriptions.completed_timers", "subscriptions.workflow_timers", "completed_at < @cutoff", CompletedTimers),
        RetentionRule.Delete("subscriptions.dead_timers", "subscriptions.workflow_timers", "dead_at < @cutoff", DeadTimers),
    ];
}
