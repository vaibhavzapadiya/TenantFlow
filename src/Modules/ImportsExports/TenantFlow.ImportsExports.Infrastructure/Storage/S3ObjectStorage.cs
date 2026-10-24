using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using TenantFlow.ImportsExports.Application.Abstractions;
using TenantFlow.ImportsExports.Domain.Files;

namespace TenantFlow.ImportsExports.Infrastructure.Storage;

/// <summary>
/// Object storage in the environment's S3 bucket (ADR-0024 amendment 1). A key is accepted only in the server's shape
/// (<see cref="StorageKeys"/>). Content is first copied to a temporary file, counting bytes and computing its SHA-256, then
/// stored with one <c>PutObject</c> carrying that checksum: S3 checks it and stores the object whole or not at all, so a
/// reader never sees half an object. The temporary file is readable by the owner only (0600) and deleted when closed.
/// Any failure to reach the bucket surfaces as <see cref="StorageUnavailableException"/>.
/// </summary>
internal sealed class S3ObjectStorage(IAmazonS3 s3, IOptions<StorageOptions> options) : IObjectStorage
{
    private const int BufferSize = 81920;

    public async Task<StoredObject> PutAsync(string key, Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        Validate(key);
        await using var copy = new FileStream(Path.Combine(Path.GetTempPath(), $"tenantflow-put-{Guid.NewGuid():N}.tmp"), CreateOptions());
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
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
            await copy.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        await copy.FlushAsync(cancellationToken);
        copy.Position = 0;
        var sha256 = hash.GetHashAndReset();
        await CallAsync(() => s3.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = options.Value.Bucket,
                Key = key,
                InputStream = copy,
                AutoCloseStream = false,
                ChecksumSHA256 = Convert.ToBase64String(sha256),
            },
            cancellationToken), cancellationToken);
        return new StoredObject(size, Convert.ToHexStringLower(sha256));
    }

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        Validate(key);
        try
        {
            var response = await CallAsync(() => s3.GetObjectAsync(options.Value.Bucket, key, cancellationToken), cancellationToken);
            return response.ResponseStream;
        }
        catch (StorageUnavailableException exception) when (exception.InnerException is AmazonS3Exception { StatusCode: HttpStatusCode.NotFound })
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        Validate(key);
        // S3 answers a delete of a missing object with success.
        await CallAsync(() => s3.DeleteObjectAsync(options.Value.Bucket, key, cancellationToken), cancellationToken);
    }

    /// <summary>Nothing to sweep: a put is never partially visible, and the bucket's lifecycle aborts unfinished uploads.</summary>
    public Task<int> DeleteTemporaryFilesAsync(DateTimeOffset olderThan, CancellationToken cancellationToken) => Task.FromResult(0);

    private static void Validate(string key)
    {
        if (!StorageKeys.IsValid(key))
        {
            throw new ArgumentException("The storage key is not one the server makes.", nameof(key));
        }
    }

    // The provider's failure, wrapped so callers handle one exception; a cancellation stays a cancellation.
    private static async Task<T> CallAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        try
        {
            return await call();
        }
        catch (Exception exception) when ((exception is AmazonServiceException or AmazonClientException or HttpRequestException or IOException)
            && !cancellationToken.IsCancellationRequested)
        {
            throw new StorageUnavailableException("The object storage call failed.", exception);
        }
    }

    private static FileStreamOptions CreateOptions()
    {
        var create = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            BufferSize = BufferSize,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose,
        };
        if (!OperatingSystem.IsWindows())
        {
            create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return create;
    }
}
