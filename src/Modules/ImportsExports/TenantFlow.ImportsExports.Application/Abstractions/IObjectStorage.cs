namespace TenantFlow.ImportsExports.Application.Abstractions;

/// <summary>
/// The object storage port (ADR-0024 decision 1). Keys are made by <c>StorageKeys</c>; an adapter refuses any other.
/// Every call may be remote: never call it inside a database transaction (AGENTS.md rule 3).
/// </summary>
public interface IObjectStorage
{
    /// <summary>
    /// Writes <paramref name="content"/> under <paramref name="key"/>, replacing what was there, and answers its size and
    /// SHA-256. A partial write is never visible under the key.
    /// </summary>
    /// <exception cref="ObjectTooLargeException">More than <paramref name="maxBytes"/> bytes; nothing was stored.</exception>
    Task<StoredObject> PutAsync(string key, Stream content, long maxBytes, CancellationToken cancellationToken);

    /// <summary>The stored content, or null when nothing is stored under the key.</summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken);

    /// <summary>Removes the object; removing a missing object is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes partial writes a crash left behind, last written before <paramref name="olderThan"/> (SPEC-0009
    /// criterion 12); answers how many. A write still in progress is younger and stays.
    /// </summary>
    Task<int> DeleteTemporaryFilesAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);
}

/// <summary>What was stored: its size and lower-case hex SHA-256.</summary>
public sealed record StoredObject(long Size, string Sha256);

/// <summary>A stream was longer than allowed; nothing was stored.</summary>
public sealed class ObjectTooLargeException : Exception
{
    public ObjectTooLargeException()
        : base("The content is larger than allowed.")
    {
    }

    public ObjectTooLargeException(string message)
        : base(message)
    {
    }

    public ObjectTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The storage could not be reached or refused the call (ADR-0024 amendment 1). The provider's detail is the inner
/// exception; it is never answered to a client.
/// </summary>
public sealed class StorageUnavailableException : Exception
{
    public StorageUnavailableException()
        : base("The object storage is unavailable.")
    {
    }

    public StorageUnavailableException(string message)
        : base(message)
    {
    }

    public StorageUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Stored content no longer matches the checksum recorded when it was written.</summary>
public sealed class StorageIntegrityException : Exception
{
    public StorageIntegrityException()
        : base("The stored content does not match its recorded checksum.")
    {
    }

    public StorageIntegrityException(string message)
        : base(message)
    {
    }

    public StorageIntegrityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
