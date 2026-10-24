using TenantFlow.BuildingBlocks.Persistence.Retention;

namespace TenantFlow.Orders.Infrastructure;

/// <summary>
/// The Orders module's retention rules (SPEC-0009 criteria 8, ADR-0025 decision 3). The Worker's retention task runs them
/// tenant by tenant; see <c>docs/security/privacy-and-retention.md</c>.
/// </summary>
public static class OrdersRetention
{
    public static readonly TimeSpan ProcessedMessages = TimeSpan.FromDays(30);

    public static readonly TimeSpan CompletedTimers = TimeSpan.FromDays(30);

    public static readonly TimeSpan DeadTimers = TimeSpan.FromDays(90);

    public static IReadOnlyList<RetentionRule> Rules { get; } =
    [
        RetentionRule.Delete("orders.processed_messages", "orders.processed_messages", "processed_at < @cutoff", ProcessedMessages),
        RetentionRule.Delete("orders.completed_timers", "orders.workflow_timers", "completed_at < @cutoff", CompletedTimers),
        RetentionRule.Delete("orders.dead_timers", "orders.workflow_timers", "dead_at < @cutoff", DeadTimers),
    ];
}
