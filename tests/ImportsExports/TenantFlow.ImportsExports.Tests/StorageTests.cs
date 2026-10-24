using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TenantFlow.ImportsExports.Application.Abstractions;
using TenantFlow.ImportsExports.Domain.Files;
using TenantFlow.ImportsExports.Infrastructure.Storage;

namespace TenantFlow.ImportsExports.Tests;

/// <summary>SPEC-0008 criterion 2: server-made keys only, nothing outside the root, size limits, whole writes.</summary>
public sealed class StorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tenantflow-storage-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Keys_are_the_servers_shape_and_name_their_tenant()
    {
        var (tenant, file) = (Guid.NewGuid(), Guid.NewGuid());

        var key = StorageKeys.For(tenant, FileKinds.Export, file);

        Assert.Equal($"tenants/{tenant:N}/export/{file:N}", key);
        Assert.Equal(tenant, StorageKeys.TenantOf(key));
        Assert.False(StorageKeys.IsValid($"tenants/{tenant:N}/export/../../etc/passwd"));
        Assert.False(StorageKeys.IsValid($"tenants/{tenant:N}/export/{file:N}/x"));
        Assert.False(StorageKeys.IsValid("invoice.csv"));
        Assert.False(StorageKeys.IsValid(key + "\n"));
        Assert.Throws<ArgumentException>(() => StorageKeys.For(tenant, "../export", file));
    }

    [Fact]
    public async Task Content_is_stored_whole_with_its_checksum_and_only_under_a_valid_key()
    {
        var storage = Storage();
        var key = StorageKeys.For(Guid.NewGuid(), FileKinds.ImportUpload, Guid.NewGuid());
        var bytes = Encoding.UTF8.GetBytes("sku,name\nA-1,Mug\n");

        var stored = await storage.PutAsync(key, new MemoryStream(bytes), maxBytes: 1024, TestContext.Current.CancellationToken);
        await using var read = await storage.OpenReadAsync(key, TestContext.Current.CancellationToken);
        using var copy = new MemoryStream();
        await read!.CopyToAsync(copy, TestContext.Current.CancellationToken);

        Assert.Equal(new StoredObject(bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))), stored);
        Assert.Equal(bytes, copy.ToArray());
        await Assert.ThrowsAsync<ArgumentException>(() => storage.PutAsync("../outside", new MemoryStream(bytes), 1024, TestContext.Current.CancellationToken));
        Assert.Null(await storage.OpenReadAsync(StorageKeys.For(Guid.NewGuid(), FileKinds.Export, Guid.NewGuid()), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Content_over_the_limit_is_refused_and_leaves_nothing_behind()
    {
        var storage = Storage();
        var key = StorageKeys.For(Guid.NewGuid(), FileKinds.ImportUpload, Guid.NewGuid());

        await Assert.ThrowsAsync<ObjectTooLargeException>(() => storage.PutAsync(key, new MemoryStream(new byte[2048]), maxBytes: 1024, TestContext.Current.CancellationToken));

        Assert.Null(await storage.OpenReadAsync(key, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    // ---- SPEC-0009 criterion 12 ----

    [Fact]
    public async Task The_sweep_deletes_only_temporary_files_left_for_more_than_an_hour()
    {
        var storage = Storage();
        var key = StorageKeys.For(Guid.NewGuid(), FileKinds.Export, Guid.NewGuid());
        await storage.PutAsync(key, new MemoryStream("sku\n"u8.ToArray()), 1024, TestContext.Current.CancellationToken);
        var folder = Path.Combine(_root, Path.GetDirectoryName(key)!);
        var now = DateTimeOffset.UtcNow;
        var left = Path.Combine(folder, $"{Path.GetFileName(key)}.{Guid.NewGuid():N}{FileSystemObjectStorage.TemporarySuffix}");
        var writing = Path.Combine(folder, $"{Path.GetFileName(key)}.{Guid.NewGuid():N}{FileSystemObjectStorage.TemporarySuffix}");
        await File.WriteAllTextAsync(left, "partial", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(writing, "partial", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(left, (now - TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1)).UtcDateTime);
        File.SetLastWriteTimeUtc(writing, (now - TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1)).UtcDateTime);

        var deleted = await storage.DeleteTemporaryFilesAsync(now - TimeSpan.FromHours(1), TestContext.Current.CancellationToken);

        Assert.Equal(1, deleted);
        Assert.Equal((false, true), (File.Exists(left), File.Exists(writing)));
        Assert.NotNull(await storage.OpenReadAsync(key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task On_unix_stored_files_are_readable_by_their_owner_only()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("File modes exist on Unix only; this runs on Linux (CI, the containers).");
        }
        else
        {
            var storage = Storage();
            var key = StorageKeys.For(Guid.NewGuid(), FileKinds.Export, Guid.NewGuid());

            await storage.PutAsync(key, new MemoryStream("sku\n"u8.ToArray()), 1024, TestContext.Current.CancellationToken);

            var file = Path.Combine(_root, key);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(file)!));
        }
    }

    private FileSystemObjectStorage Storage() => new(Options.Create(new StorageOptions { Root = _root }));
}
