using TenantFlow.BuildingBlocks.Persistence.Retention;

namespace TenantFlow.Tenants.Infrastructure;

/// <summary>
/// The Tenants module's retention rules (SPEC-0009 criteria 8 and 10, ADR-0025 decision 3). The Worker's retention task runs them
/// tenant by tenant; see <c>docs/security/privacy-and-retention.md</c>.
/// </summary>
public static class TenantsRetention
{
    /// <summary>Audit records are kept this long; the purge function in the database holds the same cutoff.</summary>
    public static readonly TimeSpan AuditRecords = TimeSpan.FromDays(400);

    public static IReadOnlyList<RetentionRule> Rules { get; } =
    [
        new("tenants.tenant_audit", AuditRecords, "SELECT tenants.purge_expired_audit()"),
    ];
}
