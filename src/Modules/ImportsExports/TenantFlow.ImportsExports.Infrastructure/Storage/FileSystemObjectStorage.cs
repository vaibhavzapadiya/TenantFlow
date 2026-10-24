using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using TenantFlow.ImportsExports.Application.Abstractions;
using TenantFlow.ImportsExports.Domain.Files;

namespace TenantFlow.ImportsExports.Infrastructure.Storage;

/// <summary>
/// Object storage on a local folder (ADR-0024 decision 1). A key is accepted only in the server's shape
/// (<see cref="StorageKeys"/>), and its full path must still lie under the root after resolution, so no key can reach
/// outside it. Content is written to a temporary file in the same folder and moved into place, so a reader never sees
/// half an object, and its SHA-256 is computed while it is copied. On Unix, files are created readable by the owner only
/// (0600) and folders (0700), and a file is flushed to disk before it is moved into place (SPEC-0009 criterion 12).
/// </summary>
internal sealed class FileSystemObjectStorage(IOptions<StorageOptions> options) : IObjectStorage
{
    private const int BufferSize = 81920;

    /// <summary>The suffix of a file being written; one left behind by a crash is deleted by the sweep.</summary>
    public const string TemporarySuffix = ".tmp";

    private const UnixFileMode FileMode0600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode DirectoryMode0700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public async Task<StoredObject> PutAsync(string key, Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = PathOf(key);
        CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}{TemporarySuffix}";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        try
        {
            await using (var target = new FileStream(temporary, CreateOptions()))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    size += read;
                    if (size > maxBytes)
                    {
                        throw new ObjectTooLargeException();
                    }

                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                // On disk before the rename makes it visible: a crash cannot leave a complete name over partial content.
                await target.FlushAsync(cancellationToken);
                target.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            return new StoredObject(size, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathOf(key);
        // Opened directly: a check first could pass for a file that expiry deletes before the open.
        try
        {
            return Task.FromResult<Stream?>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferSize, useAsync: true));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        File.Delete(PathOf(key));
        return Task.CompletedTask;
    }

    public Task<int> DeleteTemporaryFilesAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(options.Value.Root!);
        if (!Directory.Exists(root))
        {
            return Task.FromResult(0);
        }

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*" + TemporarySuffix, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.GetLastWriteTimeUtc(file) < olderThan.UtcDateTime)
            {
                File.Delete(file);
                deleted++;
            }
        }

        return Task.FromResult(deleted);
    }

    private static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, DirectoryMode0700);
        }
    }

    private static FileStreamOptions CreateOptions()
    {
        var create = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = BufferSize,
            Options = FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows())
        {
            create.UnixCreateMode = FileMode0600;
        }

        return create;
    }

    // The key's path under the root; a key of another shape, or one that would resolve outside the root, is refused.
    private string PathOf(string key)
    {
        if (!StorageKeys.IsValid(key))
        {
            throw new ArgumentException("The storage key is not one the server makes.", nameof(key));
        }

        var root = Path.GetFullPath(options.Value.Root!);
        var path = Path.GetFullPath(Path.Combine(root, key));
        return path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? path
            : throw new ArgumentException("The storage key leaves the storage root.", nameof(key));
    }
}
