using TenantFlow.BuildingBlocks.Persistence.Retention;

namespace TenantFlow.Catalog.Infrastructure;

/// <summary>
/// The Catalog module's retention rules (SPEC-0009 criteria 8, ADR-0025 decision 3). The Worker's retention task runs them
/// tenant by tenant; see <c>docs/security/privacy-and-retention.md</c>.
/// </summary>
public static class CatalogRetention
{
    public static readonly TimeSpan ImportBatches = TimeSpan.FromDays(90);

    public static IReadOnlyList<RetentionRule> Rules { get; } =
    [
        RetentionRule.Delete("catalog.import_batches", "catalog.import_batches", "applied_at < @cutoff", ImportBatches),
    ];
}
