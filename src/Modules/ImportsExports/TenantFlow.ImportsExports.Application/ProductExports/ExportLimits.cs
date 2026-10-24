namespace TenantFlow.ImportsExports.Application.ProductExports;

/// <summary>The export limits and schedule (SPEC-0008 criterion 7, ADR-0024 decision 4).</summary>
public static class ExportLimits
{
    public const int MaxRows = 100_000;

    /// <summary>Products read from Catalog per page.</summary>
    public const int PageSize = 500;

    /// <summary>A bound on the file, far above <see cref="MaxRows"/> rows of the longest names and SKUs.</summary>
    public const long MaxBytes = 512L * 1024 * 1024;

    /// <summary>Jobs of one tenant that may be active (queued or running) at once.</summary>
    public const int MaxActiveJobs = 2;

    /// <summary>How long a succeeded export's file can be downloaded.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public const string RunKind = "imports_exports.run_export";
}
