using TenantFlow.BuildingBlocks.Persistence.Retention;

namespace TenantFlow.ImportsExports.Infrastructure;

/// <summary>
/// The ImportsExports module's retention rules (SPEC-0009 criteria 8, ADR-0025 decision 3). The Worker's retention task runs them
/// tenant by tenant; see <c>docs/security/privacy-and-retention.md</c>.
/// </summary>
public static class ImportsExportsRetention
{
    /// <summary>Finished jobs, their row errors and their file rows are kept this long after the job ended.</summary>
    public static readonly TimeSpan FinishedJobs = TimeSpan.FromDays(90);

    public static readonly TimeSpan CompletedTimers = TimeSpan.FromDays(30);

    public static readonly TimeSpan DeadTimers = TimeSpan.FromDays(90);

    public static IReadOnlyList<RetentionRule> Rules { get; } =
    [
        // Row errors first, then their jobs: an error is never left without its job.
        RetentionRule.Delete("imports_exports.import_row_errors", "imports_exports.import_row_errors", "job_id IN (SELECT id FROM imports_exports.import_jobs j WHERE j.tenant_id = @tenant AND j.completed_at < @cutoff)", FinishedJobs),
        RetentionRule.Delete("imports_exports.import_jobs", "imports_exports.import_jobs", "completed_at < @cutoff", FinishedJobs),
        RetentionRule.Delete("imports_exports.export_jobs", "imports_exports.export_jobs", "completed_at < @cutoff", FinishedJobs),
        // A file row outlives its file (7 days); it goes once no job names it.
        RetentionRule.Delete("imports_exports.stored_files", "imports_exports.stored_files", "created_at < @cutoff AND NOT EXISTS (SELECT 1 FROM imports_exports.import_jobs j WHERE j.tenant_id = @tenant AND j.file_id = stored_files.id) AND NOT EXISTS (SELECT 1 FROM imports_exports.export_jobs j WHERE j.tenant_id = @tenant AND j.file_id = stored_files.id)", FinishedJobs),
        RetentionRule.Delete("imports_exports.completed_timers", "imports_exports.workflow_timers", "completed_at < @cutoff", CompletedTimers),
        RetentionRule.Delete("imports_exports.dead_timers", "imports_exports.workflow_timers", "dead_at < @cutoff", DeadTimers),
    ];
}
