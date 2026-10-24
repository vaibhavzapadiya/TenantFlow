using TenantFlow.BuildingBlocks.Abstractions.Errors;

namespace TenantFlow.ImportsExports.Domain.Jobs;

/// <summary>
/// The ImportsExports module's error catalog (ADR-0016). Codes are a public contract: never rename or reuse one.
/// Messages are fixed sentences without user input.
/// </summary>
public static class JobErrors
{
    public static readonly AppError NotFound =
        new("imports_exports.job_not_found", ErrorKind.NotFound, "The job does not exist.");

    public static readonly AppError TooManyJobs =
        new("imports_exports.too_many_jobs", ErrorKind.Conflict, "The tenant already has as many active jobs of this kind as allowed. Retry when one finishes.");

    public static readonly AppError IdempotencyKeyReused =
        new("imports_exports.idempotency_key_reused", ErrorKind.UnprocessableContent, "This Idempotency-Key was already used with a different request.");

    public static readonly AppError IdempotencyKeyInvalid =
        new("imports_exports.idempotency_key.invalid", ErrorKind.Validation, "An Idempotency-Key header of 1 to 255 visible ASCII characters is required.");

    public static readonly AppError FileTooLarge =
        new("imports_exports.file_too_large", ErrorKind.PayloadTooLarge, "The file is larger than 5 MB.");

    public static readonly AppError FileNotCsv =
        new("imports_exports.file_not_csv", ErrorKind.Validation, "A non-empty CSV file (text/csv) is required.");

    public static readonly AppError FileNotReady =
        new("imports_exports.file_not_ready", ErrorKind.NotFound, "The job has no file to download.");

    public static readonly AppError StorageUnavailable =
        new("imports_exports.storage_unavailable", ErrorKind.Unavailable, "File storage is unavailable. Retry later.");
}
