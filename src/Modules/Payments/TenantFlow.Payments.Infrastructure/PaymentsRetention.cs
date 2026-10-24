using TenantFlow.BuildingBlocks.Persistence.Retention;

namespace TenantFlow.Payments.Infrastructure;

/// <summary>
/// The Payments module's retention rules (SPEC-0009 criteria 8, ADR-0025 decision 3). The Worker's retention task runs them
/// tenant by tenant; see <c>docs/security/privacy-and-retention.md</c>.
/// </summary>
public static class PaymentsRetention
{
    public static readonly TimeSpan WebhookEvents = TimeSpan.FromDays(90);

    /// <summary>A finished key replayed later starts a new operation (providers keep theirs 24 hours).</summary>
    public static readonly TimeSpan FinishedIdempotencyKeys = TimeSpan.FromDays(30);

    public static readonly TimeSpan CompletedTimers = TimeSpan.FromDays(30);

    public static readonly TimeSpan DeadTimers = TimeSpan.FromDays(90);

    public static IReadOnlyList<RetentionRule> Rules { get; } =
    [
        RetentionRule.Delete("payments.webhook_events", "payments.webhook_events", "received_at < @cutoff", WebhookEvents),
        RetentionRule.Delete("payments.idempotency_keys", "payments.idempotency_keys", "completed_at < @cutoff", FinishedIdempotencyKeys),
        RetentionRule.Delete("payments.completed_timers", "payments.workflow_timers", "completed_at < @cutoff", CompletedTimers),
        RetentionRule.Delete("payments.dead_timers", "payments.workflow_timers", "dead_at < @cutoff", DeadTimers),
    ];
}
