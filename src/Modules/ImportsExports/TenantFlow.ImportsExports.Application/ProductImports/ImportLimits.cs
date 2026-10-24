namespace TenantFlow.ImportsExports.Application.ProductImports;

/// <summary>The import limits and schedule (SPEC-0008 criteria 3 to 5, ADR-0024 decision 4).</summary>
public static class ImportLimits
{
    public const long MaxBytes = 5 * 1024 * 1024;

    /// <summary>The upload request: the file and its multipart framing. The API refuses a larger body with 413.</summary>
    public const long MaxRequestBytes = MaxBytes + (64 * 1024);

    /// <summary>Form parts read from an upload request: the file, and nothing much else.</summary>
    public const int MaxFormParts = 4;
    public const int MaxRows = 10_000;
    public const int BatchSize = 500;

    /// <summary>Batches one Worker run applies before it gives its lease back and runs again.</summary>
    public const int BatchesPerRun = 10;

    /// <summary>Jobs of one tenant that may be active (receiving, queued or running) at once.</summary>
    public const int MaxActiveJobs = 2;

    /// <summary>
    /// An active job not updated for this long no longer counts toward <see cref="MaxActiveJobs"/>: a job whose Worker run
    /// keeps failing must not block the tenant (its last attempt ends it as <c>run_failed</c>).
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);

    /// <summary>The CSV columns, in any order; another column or a missing one fails the job.</summary>
    public static readonly IReadOnlyList<string> Columns = ["sku", "name", "price_amount", "price_currency", "status"];

    /// <summary>An upload still receiving after this is abandoned.</summary>
    public static readonly TimeSpan AbandonAfter = TimeSpan.FromHours(1);

    /// <summary>How long an upload is kept after its import ended.</summary>
    public static readonly TimeSpan UploadRetention = TimeSpan.FromDays(7);

    /// <summary>Row errors listed for a job.</summary>
    public const int MaxListedErrors = 1000;

    public const string RunKind = "imports_exports.run_import";
    public const string AbandonKind = "imports_exports.abandon_import";
}
