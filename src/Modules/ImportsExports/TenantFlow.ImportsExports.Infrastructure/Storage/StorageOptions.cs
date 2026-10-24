namespace TenantFlow.ImportsExports.Infrastructure.Storage;

/// <summary>
/// Where objects are kept (ADR-0024 decision 1 and amendment 1), bound from <c>Storage</c>: the adapter is chosen
/// explicitly, and each one needs its own keys. No credential is ever a setting: on S3 it comes from the task role.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public const string FileSystemProvider = "filesystem";

    public const string S3Provider = "s3";

    /// <summary><see cref="FileSystemProvider"/> or <see cref="S3Provider"/>. Required.</summary>
    public string? Provider { get; set; }

    /// <summary>The folder every object lives under; the API and the Worker must share it. Required for the filesystem.</summary>
    public string? Root { get; set; }

    /// <summary>
    /// The filesystem must be switched on explicitly, as the payment simulator is: deployed tasks share no disk, so only
    /// local development and tests keep files there (SPEC-0012 criterion 16).
    /// </summary>
    public bool FileSystemEnabled { get; set; }

    /// <summary>The environment's bucket. Required for S3.</summary>
    public string? Bucket { get; set; }

    /// <summary>The bucket's region, set explicitly rather than taken from the task's environment. Required for S3.</summary>
    public string? Region { get; set; }
}
