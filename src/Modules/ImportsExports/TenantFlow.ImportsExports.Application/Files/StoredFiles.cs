using System.Security.Cryptography;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.ImportsExports.Application.Abstractions;
using TenantFlow.ImportsExports.Domain.Files;

namespace TenantFlow.ImportsExports.Application.Files;

/// <summary>
/// Files of the tenant in the current tenant context (SPEC-0008 criterion 2): reserved as a row first, then written to
/// object storage outside any transaction, then recorded with size and checksum. Reading verifies the checksum as the
/// content is read, and a file is only opened through a row of the current tenant whose key names that tenant.
/// </summary>
public sealed class StoredFiles(ITenantContext context, IFileStore store, IObjectStorage storage, TimeProvider clock)
{
    /// <summary>Commits a file row for content not written yet (intent first, AGENTS.md rule 3).</summary>
    public async Task<StoredFile> ReserveAsync(string kind, string contentType, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var file = StoredFile.Reserve(Guid.CreateVersion7(now), context.TenantId, kind, contentType, now);
        await store.AddAsync(file, cancellationToken);
        return file;
    }

    /// <summary>Writes the content of a reserved file and records its size and checksum.</summary>
    /// <exception cref="ObjectTooLargeException">More than <paramref name="maxBytes"/> bytes; nothing was stored or recorded.</exception>
    public async Task<StoredObject> WriteAsync(StoredFile file, Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        Guard(file);
        var written = await storage.PutAsync(file.Key, content, maxBytes, cancellationToken);
        await store.MarkWrittenAsync(file.Id, written, cancellationToken);
        return written;
    }

    /// <summary>Writes the content of a reserved file of the current tenant, found by its id, and records it.</summary>
    /// <exception cref="ObjectTooLargeException">More than <paramref name="maxBytes"/> bytes; nothing was stored or recorded.</exception>
    public async Task<StoredObject> WriteAsync(Guid fileId, Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        var file = await store.FindAsync(fileId, cancellationToken) ?? throw new InvalidOperationException("The file row does not exist in this tenant.");
        return await WriteAsync(file, content, maxBytes, cancellationToken);
    }

    /// <summary>
    /// Writes (or overwrites) the content of a file of the current tenant without recording it: the caller records the
    /// answered size and checksum in its own transaction (exports, with the job's success).
    /// </summary>
    /// <exception cref="ObjectTooLargeException">More than <paramref name="maxBytes"/> bytes; nothing was stored.</exception>
    public async Task<StoredObject> PutAsync(Guid fileId, Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        var file = await store.FindAsync(fileId, cancellationToken) ?? throw new InvalidOperationException("The file row does not exist in this tenant.");
        Guard(file);
        return await storage.PutAsync(file.Key, content, maxBytes, cancellationToken);
    }

    /// <summary>
    /// The file's content, checked against its recorded checksum while it is read, or null when the file is not the
    /// current tenant's, not written, deleted, or missing from storage.
    /// </summary>
    public async Task<Stream?> OpenAsync(Guid fileId, CancellationToken cancellationToken)
    {
        var file = await store.FindAsync(fileId, cancellationToken);
        if (file is not { IsWritten: true })
        {
            return null;
        }

        Guard(file);
        var content = await storage.OpenReadAsync(file.Key, cancellationToken);
        return content is null ? null : new VerifyingStream(content, file.Sha256!);
    }

    /// <summary>Removes the object, then records it removed. Deleting twice is harmless.</summary>
    public async Task DeleteAsync(Guid fileId, CancellationToken cancellationToken)
    {
        if (await store.FindAsync(fileId, cancellationToken) is { DeletedAt: null } file)
        {
            Guard(file);
            await storage.DeleteAsync(file.Key, cancellationToken);
            await store.MarkDeletedAsync(file.Id, clock.GetUtcNow(), cancellationToken);
        }
    }

    // A second, independent check: the key itself must name the tenant acting now.
    private void Guard(StoredFile file)
    {
        if (StorageKeys.TenantOf(file.Key) != context.TenantId)
        {
            throw new InvalidOperationException("A stored file can only be used in the tenant its key names.");
        }
    }

    // Hashes what is read; at the end, content that differs from what was written is an error, not data.
    private sealed class VerifyingStream(Stream inner, string expectedSha256) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private bool _verified;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Account(buffer.AsSpan(offset, inner.Read(buffer, offset, count)), count);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            return Account(buffer.Span[..read], buffer.Length);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            _hash.Dispose();
            await base.DisposeAsync();
        }

        // The end is a read of 0 bytes into room for more; a read that asked for 0 bytes says nothing about the end.
        private int Account(ReadOnlySpan<byte> read, int requested)
        {
            if (read.Length > 0)
            {
                _hash.AppendData(read);
            }
            else if (requested > 0 && !_verified)
            {
                _verified = true;
                if (!string.Equals(Convert.ToHexStringLower(_hash.GetHashAndReset()), expectedSha256, StringComparison.Ordinal))
                {
                    throw new StorageIntegrityException();
                }
            }

            return read.Length;
        }
    }
}
