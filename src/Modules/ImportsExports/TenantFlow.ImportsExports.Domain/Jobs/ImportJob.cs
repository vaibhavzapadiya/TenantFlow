using TenantFlow.BuildingBlocks.Abstractions.Errors;

namespace TenantFlow.ImportsExports.Domain.Jobs;

/// <summary>
/// A product import (SPEC-0008 criteria 3 to 6, ADR-0024 decision 3): the uploaded file, how far the Worker got
/// (<see cref="CheckpointRow"/>, rows fully applied) and what it did. Submitted once per idempotency key of the tenant;
/// its request hash binds the key to the caller and the file's content.
/// </summary>
public sealed class ImportJob
{
    public const int MaxIdempotencyKeyLength = 255;

    private ImportJob()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public JobStatus Status { get; private set; }

    public Guid FileId { get; private set; }

    public string IdempotencyKey { get; private set; } = string.Empty;

    /// <summary>Lower-case hex SHA-256 of the caller's membership and the file's SHA-256.</summary>
    public string RequestHash { get; private set; } = string.Empty;

    /// <summary>Data rows applied so far; the next run starts after it.</summary>
    public int CheckpointRow { get; private set; }

    public int CreatedCount { get; private set; }

    public int UpdatedCount { get; private set; }

    public int FailedCount { get; private set; }

    public string? FailureCode { get; private set; }

    public Guid RequestedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Visible ASCII only, as RFC 9110 allows in a header value without quoting.</summary>
    public static AppError? CheckIdempotencyKey(string? key) =>
        key is { Length: > 0 and <= MaxIdempotencyKeyLength } && key.All(static c => c is > ' ' and <= '~') ? null : JobErrors.IdempotencyKeyInvalid;

    /// <summary>A job for an upload that is about to be written: <see cref="JobStatus.Receiving"/>.</summary>
    public static ImportJob Receive(Guid id, Guid tenantId, Guid fileId, string idempotencyKey, string requestHash, Guid requestedBy, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = tenantId,
        Status = JobStatus.Receiving,
        FileId = fileId,
        IdempotencyKey = idempotencyKey,
        RequestHash = requestHash,
        RequestedBy = requestedBy,
        CreatedAt = now,
        UpdatedAt = now,
    };
}
