using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.ImportsExports.Application.Abstractions;
using TenantFlow.ImportsExports.Application.Files;
using TenantFlow.ImportsExports.Domain.Files;
using TenantFlow.ImportsExports.Infrastructure;

namespace TenantFlow.Database.Tests.ImportsExports;

/// <summary>
/// SPEC-0012 criteria 18 and 19 (S3c, ADR-0024 amendment 1) against real PostgreSQL and Adobe S3Mock, with the module
/// wired as the hosts wire it for <c>Storage:Provider=s3</c>: the S3 adapter keeps the storage port's promises, a changed
/// object fails its checksum when read, and a file is only ever opened through its own tenant's row.
/// </summary>
public sealed class S3StorageTests(TenantDatabaseFixture fixture, S3MockFixture s3) : IClassFixture<TenantDatabaseFixture>, IClassFixture<S3MockFixture>
{
    [Fact]
    public async Task Put_read_and_delete_keep_the_storage_ports_promises()
    {
        var content = Encoding.UTF8.GetBytes("sku,name,price_amount,price_currency,status\nA-1,Mug,5.00,EUR,active\n");
        var key = StorageKeys.For(Guid.NewGuid(), FileKinds.Export, Guid.NewGuid());
        var tooLarge = StorageKeys.For(Guid.NewGuid(), FileKinds.ImportUpload, Guid.NewGuid());
        var missing = StorageKeys.For(Guid.NewGuid(), FileKinds.Export, Guid.NewGuid());
        var cancel = TestContext.Current.CancellationToken;

        await WithStorageAsync(async storage =>
        {
            var stored = await storage.PutAsync(key, new MemoryStream(content), 1024, cancel);
            Assert.Equal(((long)content.Length, Convert.ToHexStringLower(SHA256.HashData(content))), (stored.Size, stored.Sha256));
            Assert.Equal(content, await ReadAllAsync(await storage.OpenReadAsync(key, cancel)));

            await Assert.ThrowsAsync<ObjectTooLargeException>(() => storage.PutAsync(tooLarge, new MemoryStream(content), content.Length - 1, cancel));
            Assert.Null(await storage.OpenReadAsync(tooLarge, cancel));
            Assert.Null(await storage.OpenReadAsync(missing, cancel));
            await Assert.ThrowsAsync<ArgumentException>(() => storage.PutAsync("tenants/../escape", new MemoryStream(content), 1024, cancel));

            await storage.DeleteAsync(key, cancel);
            await storage.DeleteAsync(missing, cancel);
            Assert.Null(await storage.OpenReadAsync(key, cancel));
            Assert.Equal(0, await storage.DeleteTemporaryFilesAsync(DateTimeOffset.UtcNow, cancel));
        });
    }

    [Fact]
    public async Task Content_changed_in_the_bucket_fails_its_checksum_when_read()
    {
        var tenant = await fixture.Harness.CreateTenantAsync("S3 files");
        var file = await InTenantAsync(tenant, async files =>
        {
            var reserved = await files.ReserveAsync(FileKinds.Export, "text/csv", TestContext.Current.CancellationToken);
            await files.WriteAsync(reserved, new MemoryStream(Encoding.UTF8.GetBytes("a,b\n1,2\n")), 1024, TestContext.Current.CancellationToken);
            return reserved;
        });
        await s3.Client.PutObjectAsync(new PutObjectRequest { BucketName = S3MockFixture.Bucket, Key = file.Key, ContentBody = "a,b\n9,9\n" }, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<StorageIntegrityException>(() => InTenantAsync(tenant, async files =>
            await ReadAllAsync(await files.OpenAsync(file.Id, TestContext.Current.CancellationToken))));
    }

    [Fact]
    public async Task Another_tenant_can_neither_open_nor_delete_a_file_in_the_bucket()
    {
        var (a, b) = (await fixture.Harness.CreateTenantAsync("S3 files A"), await fixture.Harness.CreateTenantAsync("S3 files B"));
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
        Assert.Equal([1, 2, 3], await InTenantAsync(a, async files => await ReadAllAsync(await files.OpenAsync(file.Id, TestContext.Current.CancellationToken))));
        var stillThere = await s3.Client.GetObjectMetadataAsync(S3MockFixture.Bucket, file.Key, TestContext.Current.CancellationToken);
        Assert.Equal(3, stillThere.ContentLength);
    }

    private static async Task<byte[]> ReadAllAsync(Stream? content)
    {
        Assert.NotNull(content);
        await using (content)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, TestContext.Current.CancellationToken);
            return copy.ToArray();
        }
    }

    private Task<bool> WithStorageAsync(Func<IObjectStorage, Task> action) =>
        InScopeAsync(null, async services =>
        {
            await action(services.GetRequiredService<IObjectStorage>());
            return true;
        });

    private Task<T> InTenantAsync<T>(Guid tenant, Func<StoredFiles, Task<T>> action) =>
        InScopeAsync(tenant, services => action(services.GetRequiredService<StoredFiles>()));

    // The module wired as the hosts wire it for S3, with S3Mock's client in place of the task role's.
    private async Task<T> InScopeAsync<T>(Guid? tenant, Func<IServiceProvider, Task<T>> action)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "s3",
            ["Storage:S3:Bucket"] = S3MockFixture.Bucket,
            ["Storage:S3:Region"] = S3MockFixture.Region,
        }).Build());
        services.AddSingleton<IAmazonS3>(s3.Client);
        services.AddImportsExportsModule(fixture.Database.AppConnectionString);
        if (tenant is { } id)
        {
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(id));
        }

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }
}
