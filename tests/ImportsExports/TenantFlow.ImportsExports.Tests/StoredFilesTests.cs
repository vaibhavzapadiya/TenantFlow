using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.ImportsExports.Application.Abstractions;
using TenantFlow.ImportsExports.Application.Files;
using TenantFlow.ImportsExports.Domain.Files;
using TenantFlow.ImportsExports.Infrastructure.Storage;

namespace TenantFlow.ImportsExports.Tests;

/// <summary>
/// SPEC-0008 criteria 2 and 9: the storage key's own tenant check, independent of the file row's filter and row-level
/// security; and checksum verification that only an end of stream triggers.
/// </summary>
public sealed class StoredFilesTests : IDisposable
{
    private static readonly byte[] Content = "sku,name\nA-1,Mug\n"u8.ToArray();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tenantflow-storedfiles-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_file_row_whose_key_names_another_tenant_is_never_opened_written_or_deleted()
    {
        var (tenant, other) = (Guid.NewGuid(), Guid.NewGuid());
        var foreign = await WrittenAsync(other);
        var files = Files(tenant, foreign);
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<InvalidOperationException>(() => files.OpenAsync(foreign.Id, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.PutAsync(foreign.Id, new MemoryStream(Content), 1024, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.DeleteAsync(foreign.Id, ct));
        Assert.NotNull(await Storage().OpenReadAsync(foreign.Key, ct));
    }

    [Fact]
    public async Task A_read_of_zero_bytes_is_not_taken_for_the_end_of_the_file()
    {
        var tenant = Guid.NewGuid();
        var file = await WrittenAsync(tenant);
        var ct = TestContext.Current.CancellationToken;

        await using var content = (await Files(tenant, file).OpenAsync(file.Id, ct))!;
        var empty = await content.ReadAsync(Memory<byte>.Empty, ct);
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy, ct);

        Assert.Equal(0, empty);
        Assert.Equal(Content, copy.ToArray());
    }

    // A file of the tenant, stored and recorded as written.
    private async Task<StoredFile> WrittenAsync(Guid tenant)
    {
        var file = StoredFile.Reserve(Guid.NewGuid(), tenant, FileKinds.Export, "text/csv", DateTimeOffset.UtcNow);
        await Storage().PutAsync(file.Key, new MemoryStream(Content), 1024, TestContext.Current.CancellationToken);
        // Only persistence records a write; the test sets what the row would hold.
        typeof(StoredFile).GetProperty(nameof(StoredFile.Size))!.SetValue(file, (long?)Content.Length);
        typeof(StoredFile).GetProperty(nameof(StoredFile.Sha256))!.SetValue(file, Convert.ToHexStringLower(SHA256.HashData(Content)));
        return file;
    }

    private StoredFiles Files(Guid tenant, StoredFile row) => new(new Tenant(tenant), new OneRow(row), Storage(), TimeProvider.System);

    private FileSystemObjectStorage Storage() => new(Options.Create(new StorageOptions { Root = _root }));

    // A store whose filter failed: it answers this row for any id, whatever the tenant.
    private sealed class OneRow(StoredFile row) : IFileStore
    {
        public Task AddAsync(StoredFile file, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<StoredFile?> FindAsync(Guid fileId, CancellationToken cancellationToken) => Task.FromResult<StoredFile?>(row);

        public Task MarkWrittenAsync(Guid fileId, StoredObject written, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task MarkDeletedAsync(Guid fileId, DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Tenant(Guid tenantId) : ITenantContext
    {
        public bool IsSet => true;

        public Guid TenantId => tenantId;

        public Guid MembershipId => Guid.Empty;

        public string Role => "tenant-admin";

        public bool HasPermission(string permission) => true;
    }
}
