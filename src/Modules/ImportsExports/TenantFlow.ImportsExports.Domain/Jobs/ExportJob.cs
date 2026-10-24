namespace TenantFlow.ImportsExports.Domain.Jobs;

/// <summary>
/// A product export (SPEC-0008 criteria 7 and 9, ADR-0024 decision 3): the file it writes, how many rows it wrote and until
/// when the file can be downloaded. Queued at once (there is no upload to receive); a crash restarts it from the start, and
/// the restart overwrites the same file. Submitted once per idempotency key of the tenant; its request hash binds the key
/// to the caller.
/// </summary>
public sealed class ExportJob
{
    private ExportJob()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public JobStatus Status { get; private set; }

    public Guid FileId { get; private set; }

    public string IdempotencyKey { get; private set; } = string.Empty;

    /// <summary>Lower-case hex SHA-256 of the caller's membership and what is exported.</summary>
    public string RequestHash { get; private set; } = string.Empty;

    /// <summary>Products written; set when the export succeeds.</summary>
    public int RowCount { get; private set; }

    public string? FailureCode { get; private set; }

    public Guid RequestedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>When the file stops being downloadable; set when the export succeeds.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>Whether the file can be downloaded at <paramref name="now"/>.</summary>
    public bool IsDownloadable(DateTimeOffset now) => Status == JobStatus.Succeeded && now < ExpiresAt;

    /// <summary>A job waiting for the Worker: <see cref="JobStatus.Queued"/>.</summary>
    public static ExportJob Queue(Guid id, Guid tenantId, Guid fileId, string idempotencyKey, string requestHash, Guid requestedBy, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = tenantId,
        Status = JobStatus.Queued,
        FileId = fileId,
        IdempotencyKey = idempotencyKey,
        RequestHash = requestHash,
        RequestedBy = requestedBy,
        CreatedAt = now,
        UpdatedAt = now,
    };
}
