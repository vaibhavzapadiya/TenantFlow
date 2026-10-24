using TenantFlow.BuildingBlocks.Messaging.Timers;
using TenantFlow.ImportsExports.Application.Files;
using TenantFlow.ImportsExports.Application.Observability;
using TenantFlow.ImportsExports.Application.ProductExports;
using TenantFlow.ImportsExports.Application.ProductImports;
using TenantFlow.ImportsExports.Infrastructure.Persistence;

namespace TenantFlow.ImportsExports.Infrastructure.Observability;

/// <summary>
/// The <c>imports_exports.metrics</c> task (SPEC-0008 criterion 11): a snapshot of the module's due rows per kind, the rows
/// due now and the oldest due time, read through the timer store, which alone names the table (ADR-0022 decision 7). It
/// reads kinds and times, never a tenant or subject id, so the task reads no tenant data. Every kind is reported, with 0
/// when none is due.
/// </summary>
internal sealed class DueJobMetrics(IEnumerable<WorkflowTimerStore> stores, ImportsExportsMetrics metrics, TimeProvider clock)
{
    public const string TaskName = "imports_exports.metrics";

    /// <summary>Every 30 seconds, on the half minute.</summary>
    public const string CronExpression = "0/30 * * * * ?";

    private static readonly string[] Kinds = [ImportLimits.RunKind, ImportLimits.AbandonKind, ExportLimits.RunKind, FileExpiry.Kind];

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var store = stores.Single(static s => s.Module == ImportsExportsDbContext.Schema);
        var due = await store.DueByKindAsync(now, cancellationToken);
        metrics.RecordDue(Kinds.Select(kind => due.SingleOrDefault(d => d.Kind == kind) is { } row ? (kind, row.Due, row.Oldest) : (kind, 0L, now)), now);
    }
}
