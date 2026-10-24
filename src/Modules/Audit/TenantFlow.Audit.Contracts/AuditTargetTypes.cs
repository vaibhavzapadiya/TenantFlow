namespace TenantFlow.Audit.Contracts;

/// <summary>What an audited action was done to, as a closed list (ADR-0026). The target is always an id, never a name.</summary>
public static class AuditTargetTypes
{
    public const string Membership = "membership";
    public const string Tenant = "tenant";
    public const string ImportJob = "import_job";
    public const string ExportJob = "export_job";
    public const string Refund = "refund";
    public const string Order = "order";
    public const string SupportGrant = "support_grant";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Membership, Tenant, ImportJob, ExportJob, Refund, Order, SupportGrant };
}
