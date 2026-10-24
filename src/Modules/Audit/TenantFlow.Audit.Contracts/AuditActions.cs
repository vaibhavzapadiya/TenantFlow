namespace TenantFlow.Audit.Contracts;

/// <summary>
/// The closed list of audited actions (SPEC-0009 criterion 2, ADR-0026). A code is public contract: it is never renamed
/// or reused, and a new audited action adds a code here. The Audit consumer refuses any other.
/// </summary>
public static class AuditActions
{
    public const string MembershipAdded = "membership.added";
    public const string MembershipRoleChanged = "membership.role_changed";
    public const string MembershipRemoved = "membership.removed";
    public const string TenantCreated = "tenant.created";
    public const string TenantSuspended = "tenant.suspended";
    public const string TenantReactivated = "tenant.reactivated";
    public const string ImportRequested = "import.requested";
    public const string ExportRequested = "export.requested";
    public const string ExportDownloaded = "export.downloaded";
    public const string RefundRequested = "refund.requested";

    /// <summary>A paid order's cancellation was requested; its refund follows (SPEC-0007 criterion 14).</summary>
    public const string OrderCancellationRequested = "order.cancellation_requested";

    /// <summary>A support operator opened a time-limited grant for the tenant (SPEC-0009 criterion 16).</summary>
    public const string SupportGrantOpened = "support.grant_opened";

    /// <summary>A support operator read the tenant through a support view (SPEC-0009 criterion 17).</summary>
    public const string SupportTenantAccessed = "support.tenant_accessed";

    /// <summary>A grant was ended by its operator or revoked by a platform administrator (SPEC-0009 criterion 18).</summary>
    public const string SupportGrantEnded = "support.grant_ended";

    /// <summary>Every known code; the longest fits the 64-character column.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        MembershipAdded,
        MembershipRoleChanged,
        MembershipRemoved,
        TenantCreated,
        TenantSuspended,
        TenantReactivated,
        ImportRequested,
        ExportRequested,
        ExportDownloaded,
        RefundRequested,
        OrderCancellationRequested,
        SupportGrantOpened,
        SupportTenantAccessed,
        SupportGrantEnded,
    };
}
