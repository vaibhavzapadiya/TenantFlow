using TenantFlow.BuildingBlocks.Messaging.Timers;
using TenantFlow.Payments.Application.Observability;
using TenantFlow.Payments.Application.Payments.Services;
using TenantFlow.Payments.Application.Refunds.Services;
using TenantFlow.Payments.Infrastructure.Persistence;

namespace TenantFlow.Payments.Infrastructure.Observability;

/// <summary>
/// The <c>payments.metrics</c> task (SPEC-0010 criterion 4): a snapshot of the payments and refunds that reconciliation
/// checked at least once and that still have no final outcome, with the oldest one's creation time, read through the timer
/// store (kinds and times only, no tenant data). Both kinds are reported, with 0 when none is unresolved.
/// </summary>
internal sealed class UnresolvedPaymentMetrics(IEnumerable<WorkflowTimerStore> stores, PaymentsMetrics metrics, TimeProvider clock)
{
    public const string TaskName = "payments.metrics";

    /// <summary>Every 30 seconds, on the quarter minute (apart from the imports and exports task).</summary>
    public const string CronExpression = "15/30 * * * * ?";

    private static readonly (string TimerKind, string Kind)[] Kinds =
    [
        (PaymentReconciliation.TimerKind, PaymentsMetrics.PaymentKind),
        (RefundReconciliation.TimerKind, PaymentsMetrics.RefundKind),
    ];

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var store = stores.Single(static s => s.Module == PaymentsDbContext.Schema);
        var unresolved = await store.UnresolvedByKindAsync(cancellationToken);
        metrics.RecordUnresolved(
            Kinds.Select(k => unresolved.SingleOrDefault(u => u.Kind == k.TimerKind) is { } row ? (k.Kind, row.Due, row.Oldest) : (k.Kind, 0L, now)),
            now);
    }
}
