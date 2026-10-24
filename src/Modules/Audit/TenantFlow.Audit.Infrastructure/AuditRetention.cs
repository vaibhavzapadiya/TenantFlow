using TenantFlow.BuildingBlocks.Persistence.Retention;

namespace TenantFlow.Audit.Infrastructure;

/// <summary>
/// The Audit module's retention rules (SPEC-0009 criteria 8 and 10, ADR-0025 decision 3). The Worker's retention task runs them
/// tenant by tenant; see <c>docs/security/privacy-and-retention.md</c>.
/// </summary>
public static class AuditRetention
{
    public static readonly TimeSpan ProcessedMessages = TimeSpan.FromDays(30);

    /// <summary>Audit records are kept this long; the purge function in the database holds the same cutoff.</summary>
    public static readonly TimeSpan AuditRecords = TimeSpan.FromDays(400);

    public static IReadOnlyList<RetentionRule> Rules { get; } =
    [
        RetentionRule.Delete("audit.processed_messages", "audit.processed_messages", "processed_at < @cutoff", ProcessedMessages),
        // Deletes only records older than the function's own fixed cutoff; the app role cannot delete them (criterion 3).
        new("audit.audit_records", AuditRecords, "SELECT audit.purge_expired()"),
    ];
}
