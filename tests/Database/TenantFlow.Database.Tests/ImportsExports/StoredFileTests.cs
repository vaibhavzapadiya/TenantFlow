using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.ImportsExports.Application.Abstractions;
using TenantFlow.ImportsExports.Application.Files;
using TenantFlow.ImportsExports.Domain.Files;
using TenantFlow.ImportsExports.Infrastructure;

namespace TenantFlow.Database.Tests.ImportsExports;

/// <summary>
/// SPEC-0008 criterion 2 against real PostgreSQL and a temporary storage folder: a file row is committed before its
/// content, the content is recorded with size and checksum, reading verifies the checksum, and a file is only ever opened
/// through its own tenant's row.
/// </summary>
public sealed class StoredFileTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tenantflow-files-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_file_is_reserved_written_with_its_checksum_and_read_back_verified()
    {
        var tenant = await fixture.Harness.CreateTenantAsync("Files");
        var content = Encoding.UTF8.GetBytes("sku,name,price_amount,price_currency,status\nA-1,Mug,5.00,EUR,active\n");

        var (file, written) = await InTenantAsync(tenant, async files =>
        {
            var reserved = await files.ReserveAsync(FileKinds.ImportUpload, "text/csv", TestContext.Current.CancellationToken);
            return (reserved, await files.WriteAsync(reserved, new MemoryStream(content), 1024, TestContext.Current.CancellationToken));
        });
        var read = await InTenantAsync(tenant, files => ReadAllAsync(files, file.Id));

        Assert.Equal(content, read);
        Assert.Equal(StorageKeys.For(tenant, FileKinds.ImportUpload, file.Id), file.Key);
        Assert.Equal((long)content.Length, written.Size);
        var row = await InTenantAsync(tenant, services => services.GetRequiredService<IFileStore>().FindAsync(file.Id, TestContext.Current.CancellationToken));
        Assert.Equal((written.Size, written.Sha256), (row!.Size, row.Sha256));
    }

    [Fact]
    public async Task Content_changed_in_storage_fails_its_checksum_when_read()
    {
        var tenant = await fixture.Harness.CreateTenantAsync("Files");
        var file = await InTenantAsync(tenant, async files =>
        {
            var reserved = await files.ReserveAsync(FileKinds.Export, "text/csv", TestContext.Current.CancellationToken);
            await files.WriteAsync(reserved, new MemoryStream(Encoding.UTF8.GetBytes("a,b\n1,2\n")), 1024, TestContext.Current.CancellationToken);
            return reserved;
        });
        await File.WriteAllTextAsync(Path.Combine(_root, file.Key), "a,b\n9,9\n", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<StorageIntegrityException>(() => InTenantAsync(tenant, files => ReadAllAsync(files, file.Id)));
    }

    [Fact]
    public async Task Another_tenant_can_neither_open_nor_delete_a_file()
    {
        var (a, b) = (await fixture.Harness.CreateTenantAsync("Files A"), await fixture.Harness.CreateTenantAsync("Files B"));
        var file = await InTenantAsync(a, async files =>
        {
            var reserved = await files.ReserveAsync(FileKinds.Export, "text/csv", TestContext.Current.CancellationToken);
            await files.WriteAsync(reserved, new MemoryStream([1, 2, 3]), 1024, TestContext.Current.CancellationToken);
            return reserved;
        });

        var opened = await InTenantAsync(b, async files => await files.OpenAsync(file.Id, TestContext.Current.CancellationToken));
        await InTenantAsync(b, async files =>
        {
            await files.DeleteAsync(file.Id, TestContext.Current.CancellationToken);
            return true;
        });

        Assert.Null(opened);
        Assert.Equal([1, 2, 3], await InTenantAsync(a, files => ReadAllAsync(files, file.Id)));
        Assert.True(File.Exists(Path.Combine(_root, file.Key)));
    }

    private static async Task<byte[]> ReadAllAsync(StoredFiles files, Guid fileId)
    {
        await using var stream = await files.OpenAsync(fileId, TestContext.Current.CancellationToken);
        Assert.NotNull(stream);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, TestContext.Current.CancellationToken);
        return copy.ToArray();
    }

    private Task<T> InTenantAsync<T>(Guid tenant, Func<StoredFiles, Task<T>> action) =>
        InTenantAsync(tenant, services => action(services.GetRequiredService<StoredFiles>()));

    // The module wired as the hosts wire it, in a scope acting for one tenant.
    private async Task<T> InTenantAsync<T>(Guid tenant, Func<IServiceProvider, Task<T>> action)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Provider"] = "filesystem", ["Storage:Filesystem:Enabled"] = "true", ["Storage:Root"] = _root }).Build());
        services.AddImportsExportsModule(fixture.Database.AppConnectionString);
        services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenant));
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }
}
