namespace TenantFlow.ImportsExports.Domain.Files;

/// <summary>
/// A file in object storage and what TenantFlow knows about it (SPEC-0008 criterion 2, ADR-0024 decision 1): its tenant,
/// kind, server-made key, and, once written, its size and SHA-256. The row is committed before the file is written
/// (intent first, AGENTS.md rule 3); until then <see cref="Size"/> and <see cref="Sha256"/> are null.
/// </summary>
public sealed class StoredFile
{
    private StoredFile()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary><see cref="FileKinds.ImportUpload"/> or <see cref="FileKinds.Export"/>.</summary>
    public string Kind { get; private set; } = string.Empty;

    public string Key { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    /// <summary>Bytes stored; null until written.</summary>
    public long? Size { get; private set; }

    /// <summary>Lower-case hex SHA-256 of the content; null until written.</summary>
    public string? Sha256 { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the file is deleted (exports and finished uploads, ADR-0024 decision 4); null until known.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public bool IsWritten => Size is not null && Sha256 is not null && DeletedAt is null;

    /// <exception cref="ArgumentException">An id is empty or the kind is unknown: a programming error.</exception>
    public static StoredFile Reserve(Guid id, Guid tenantId, string kind, string contentType, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = tenantId,
        Kind = kind,
        Key = StorageKeys.For(tenantId, kind, id),
        ContentType = contentType,
        CreatedAt = now,
    };
}
