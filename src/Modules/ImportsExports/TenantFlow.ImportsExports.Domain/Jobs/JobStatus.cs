namespace TenantFlow.ImportsExports.Domain.Jobs;

/// <summary>Where an import or export job is (SPEC-0008, ADR-0024 decision 3).</summary>
public enum JobStatus
{
    /// <summary>Its file is being received (imports): committed before the upload is written.</summary>
    Receiving = 1,

    /// <summary>Waiting for the Worker.</summary>
    Queued = 2,

    /// <summary>The Worker works on it; a crash resumes it.</summary>
    Running = 3,

    /// <summary>Done. Final.</summary>
    Succeeded = 4,

    /// <summary>Stopped with a failure code. Final.</summary>
    Failed = 5,
}

/// <summary>The stable names a <see cref="JobStatus"/> has in the API and the database.</summary>
public static class JobStatusNames
{
    public const string Receiving = "receiving";
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    public static readonly IReadOnlyList<string> All = [Receiving, Queued, Running, Succeeded, Failed];

    /// <summary>The names of jobs that still hold one of the tenant's active-job places.</summary>
    public static readonly IReadOnlyList<string> Active = [Receiving, Queued, Running];

    public static string ToName(this JobStatus status) => status switch
    {
        JobStatus.Receiving => Receiving,
        JobStatus.Queued => Queued,
        JobStatus.Running => Running,
        JobStatus.Succeeded => Succeeded,
        JobStatus.Failed => Failed,
        _ => throw new InvalidOperationException("The job status is not one the domain defines."),
    };

    public static JobStatus Parse(string name) => name switch
    {
        Receiving => JobStatus.Receiving,
        Queued => JobStatus.Queued,
        Running => JobStatus.Running,
        Succeeded => JobStatus.Succeeded,
        Failed => JobStatus.Failed,
        _ => throw new InvalidOperationException("The stored job status is not a known status name."),
    };
}

/// <summary>Why a job failed: short, stable codes; never data from the file.</summary>
public static class JobFailureCodes
{
    public const string InvalidHeader = "invalid_header";
    public const string TooManyRows = "too_many_rows";
    public const string UploadAbandoned = "upload_abandoned";
    public const string FileUnreadable = "file_unreadable";
    public const string FileMissing = "file_missing";
    public const string FileCorrupt = "file_corrupt";
    public const string FileTooLarge = "file_too_large";

    /// <summary>The job's run failed on every attempt the Worker allows (ADR-0023): ended so it frees its place.</summary>
    public const string RunFailed = "run_failed";

    /// <summary>The tenant was suspended after the job was submitted (SPEC-0009 criterion 11).</summary>
    public const string TenantSuspended = "tenant_suspended";

    /// <summary>The requester was removed or no longer holds the permission the job needs (SPEC-0009 criterion 11).</summary>
    public const string RequesterNotAuthorized = "requester_not_authorized";
}
