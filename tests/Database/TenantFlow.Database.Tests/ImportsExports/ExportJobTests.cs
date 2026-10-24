using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TenantFlow.ImportsExports.Application.Abstractions;
using TenantFlow.ImportsExports.Domain.Files;

namespace TenantFlow.Database.Tests.ImportsExports;

/// <summary>
/// SPEC-0008 criteria 7 to 9 against real PostgreSQL: product exports submitted and downloaded through the real API and run
/// by the Worker's modules from their due rows (<see cref="WorkerHarness"/>), as Quartz would run them.
/// </summary>
public sealed class ExportJobTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
{
    private const string Header = "\"sku\",\"name\",\"price_amount\",\"price_currency\",\"status\"\r\n";

    private WebApplicationFactory<Program>? _factory;
    private WorkerHarness? _worker;
    private PaymentScenario? _scenario;

    private PaymentScenario S => _scenario ?? throw new InvalidOperationException("Not initialized.");

    private WorkerHarness Worker => _worker ?? throw new InvalidOperationException("Not initialized.");

    public async ValueTask InitializeAsync()
    {
        _factory = ApiFactory.CreateProduction(connectionString: fixture.Database.AppConnectionString);
        _worker = WorkerHarness.Create(fixture.Database);
        _scenario = await PaymentScenario.CreateAsync(fixture, _factory);
    }

    public async ValueTask DisposeAsync()
    {
        if (_worker is not null)
        {
            await _worker.DisposeAsync();
        }

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    // ---- criteria 7 and 8 ----

    [Fact]
    public async Task An_export_writes_every_product_with_formulas_neutralized_and_its_file_imports_back_unchanged()
    {
        await CreateProductAsync("EXP-1", "=HYPERLINK(\"x\")");
        await CreateProductAsync("EXP-2", "@SUM(A1)");
        var archived = await CreateProductAsync("EXP-3", "Plain");
        await S.ScalarAsync(S.TenantA, "UPDATE catalog.products SET status = 'archived' WHERE id = @id RETURNING id", archived);
        var products = await S.CountAsync(S.TenantA, "SELECT count(*) FROM catalog.products WHERE @id IS NOT NULL", Guid.Empty);

        using var submitted = await SubmitAsync(S.AdminA, Key());
        var queued = await PaymentScenario.JsonAsync(submitted);
        await RunAsync();
        var done = await StatusJsonAsync(submitted);
        using var file = await S.SendAsync(HttpMethod.Get, $"{submitted.Headers.Location}/file", S.AdminA);
        var csv = await file.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal((HttpStatusCode.Accepted, "queued"), (submitted.StatusCode, queued.GetProperty("status").GetString()));
        Assert.Equal(("succeeded", (int)products), (done.GetProperty("status").GetString(), done.GetProperty("rowCount").GetInt32()));
        Assert.Equal(done.GetProperty("completedAt").GetDateTimeOffset() + TimeSpan.FromDays(7), done.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal((HttpStatusCode.OK, "text/csv"), (file.StatusCode, file.Content.Headers.ContentType?.MediaType));
        Assert.StartsWith(Header, csv, StringComparison.Ordinal);
        Assert.Matches("\"EXP-1\",\"'=HYPERLINK\\(\"\"x\"\"\\)\",\"1\\.0000\",\"EUR\",\"active\"\r\n", csv);
        Assert.Contains("\"EXP-2\",\"'@SUM(A1)\",", csv, StringComparison.Ordinal);
        Assert.Matches("\"EXP-3\",\"Plain\",\"1\\.0000\",\"EUR\",\"archived\"\r\n", csv);
        // SPEC-0009 criterion 2: the request and the download are each audited once, naming the member by membership id.
        var exportId = queued.GetProperty("id").GetGuid();
        const string exportRequester = "SELECT requested_by FROM imports_exports.export_jobs WHERE id = @id";
        Assert.Equal(1, await S.CountAsync(S.TenantA, AuditOutbox.Count("imports_exports", "export.requested", "export_job", exportRequester), exportId));
        Assert.Equal(1, await S.CountAsync(S.TenantA, AuditOutbox.Count("imports_exports", "export.downloaded", "export_job", exportRequester), exportId));

        // The file imports back: every product updated, the formula names as they were.
        using var imported = await ImportAsync(csv);
        await RunAsync();
        using var import = await S.SendAsync(HttpMethod.Get, imported.Headers.Location!.ToString(), S.AdminA);
        var importDone = await PaymentScenario.JsonAsync(import);
        Assert.Equal(("succeeded", 0, (int)products, 0), (importDone.GetProperty("status").GetString(), importDone.GetProperty("created").GetInt32(),
            importDone.GetProperty("updated").GetInt32(), importDone.GetProperty("failed").GetInt32()));
        Assert.Equal("=HYPERLINK(\"x\")", await S.TextAsync(S.TenantA, "SELECT name FROM catalog.products WHERE sku = 'EXP-1' AND @id IS NOT NULL", Guid.Empty));
        Assert.Equal("@SUM(A1)", await S.TextAsync(S.TenantA, "SELECT name FROM catalog.products WHERE sku = 'EXP-2' AND @id IS NOT NULL", Guid.Empty));
    }

    // ---- criterion 7 ----

    [Fact]
    public async Task Submission_is_idempotent_per_member_bounded_and_for_managers_only()
    {
        var otherAdmin = $"admin2-{Guid.NewGuid():N}";
        await fixture.Harness.AddAsync(S.TenantA, otherAdmin, "tenant-admin", TestIssuer.Issuer);
        var key = Key();
        using var first = await SubmitAsync(S.AdminA, key);
        using var again = await SubmitAsync(S.AdminA, key);
        using var byOtherAdmin = await SubmitAsync(otherAdmin, key);
        using var second = await SubmitAsync(S.AdminA, Key());
        using var third = await SubmitAsync(S.AdminA, Key());
        using var byCustomer = await SubmitAsync(S.CustomerA, Key());
        using var noKey = await SubmitAsync(S.AdminA, null);

        Assert.Equal((HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.Accepted), (first.StatusCode, again.StatusCode, second.StatusCode));
        Assert.Equal((await PaymentScenario.JsonAsync(first)).GetProperty("id").GetGuid(), (await PaymentScenario.JsonAsync(again)).GetProperty("id").GetGuid());
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "imports_exports.idempotency_key_reused"), (byOtherAdmin.StatusCode, await PaymentScenario.CodeAsync(byOtherAdmin)));
        Assert.Equal((HttpStatusCode.Conflict, "imports_exports.too_many_jobs"), (third.StatusCode, await PaymentScenario.CodeAsync(third)));
        Assert.Equal(HttpStatusCode.Forbidden, byCustomer.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
    }

    [Fact]
    public async Task An_export_that_crashed_while_writing_restarts_and_overwrites_its_file()
    {
        await CreateProductAsync("CRASH-1", "One");
        using var submitted = await SubmitAsync(S.AdminA, Key());
        var jobId = (await PaymentScenario.JsonAsync(submitted)).GetProperty("id").GetGuid();
        var fileId = Guid.Parse((await S.TextAsync(S.TenantA, "SELECT file_id::text FROM imports_exports.export_jobs WHERE id = @id", jobId))!);

        // A run started, left content under the key, and crashed before recording anything.
        await Worker.InTenantAsync(S.TenantA, async services =>
        {
            var ct = TestContext.Current.CancellationToken;
            await services.GetRequiredService<IObjectStorage>().PutAsync(StorageKeys.For(S.TenantA, FileKinds.Export, fileId), new MemoryStream("partial"u8.ToArray()), 100, ct);
            return await services.GetRequiredService<IExportJobStore>().StartRunningAsync(jobId, DateTimeOffset.UtcNow, ct);
        });
        await RunAsync();
        using var file = await S.SendAsync(HttpMethod.Get, $"{submitted.Headers.Location}/file", S.AdminA);
        var csv = await file.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal("succeeded", (await StatusJsonAsync(submitted)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.StartsWith(Header, csv, StringComparison.Ordinal);
        Assert.Contains("\"CRASH-1\",\"One\",", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("partial", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_file_is_not_ready_before_the_export_succeeds_nor_after_it_expires()
    {
        using var submitted = await SubmitAsync(S.AdminA, Key());
        var jobId = (await PaymentScenario.JsonAsync(submitted)).GetProperty("id").GetGuid();
        using var early = await S.SendAsync(HttpMethod.Get, $"{submitted.Headers.Location}/file", S.AdminA);
        await RunAsync();
        using var ready = await S.SendAsync(HttpMethod.Get, $"{submitted.Headers.Location}/file", S.AdminA);
        await S.ScalarAsync(S.TenantA, "UPDATE imports_exports.export_jobs SET expires_at = now() - interval '1 second' WHERE id = @id RETURNING id", jobId);
        using var expired = await S.SendAsync(HttpMethod.Get, $"{submitted.Headers.Location}/file", S.AdminA);

        Assert.Equal((HttpStatusCode.NotFound, "imports_exports.file_not_ready"), (early.StatusCode, await PaymentScenario.CodeAsync(early)));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal((HttpStatusCode.NotFound, "imports_exports.file_not_ready"), (expired.StatusCode, await PaymentScenario.CodeAsync(expired)));
    }

    // ---- criterion 9 ----

    [Fact]
    public async Task Another_tenant_can_neither_read_an_export_nor_download_its_file()
    {
        using var submitted = await SubmitAsync(S.AdminA, Key());
        var jobId = (await PaymentScenario.JsonAsync(submitted)).GetProperty("id").GetGuid();
        await RunAsync();

        using var read = await S.SendAsync(HttpMethod.Get, $"/v1/tenants/{S.TenantB}/exports/{jobId}", S.AdminB);
        using var file = await S.SendAsync(HttpMethod.Get, $"/v1/tenants/{S.TenantB}/exports/{jobId}/file", S.AdminB);

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (read.StatusCode, file.StatusCode));
        Assert.Equal(("imports_exports.job_not_found", "imports_exports.job_not_found"), (await PaymentScenario.CodeAsync(read), await PaymentScenario.CodeAsync(file)));
    }

    // ---- SPEC-0012 amendment 1, question 19 ----

    [Fact]
    public async Task A_download_answers_503_without_provider_detail_while_storage_is_unavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        using var submitted = await SubmitAsync(S.AdminA, Key());
        await RunAsync();
        await using var unavailable = ApiFactory.CreateProduction(
            static services => services.AddSingleton<IObjectStorage>(new UnavailableStorage()),
            fixture.Database.AppConnectionString);
        using var client = unavailable.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{submitted.Headers.Location}/file");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Shared.CreateToken(S.AdminA));

        using var file = await client.SendAsync(request, ct);
        var body = await file.Content.ReadAsStringAsync(ct);

        Assert.Equal((HttpStatusCode.ServiceUnavailable, "imports_exports.storage_unavailable"), (file.StatusCode, await PaymentScenario.CodeAsync(file)));
        Assert.DoesNotContain(UnavailableStorage.ProviderDetail, body, StringComparison.Ordinal);
    }

    // ---- SPEC-0009 criterion 11 ----

    [Fact]
    public async Task An_export_ends_unwritten_when_its_requester_is_demoted_or_removed_or_its_tenant_suspended()
    {
        var demoted = await fixture.Harness.AddAsync(S.TenantA, $"demoted-{Guid.NewGuid():N}", "tenant-admin", TestIssuer.Issuer);
        var removed = await fixture.Harness.AddAsync(S.TenantA, $"removed-{Guid.NewGuid():N}", "tenant-admin", TestIssuer.Issuer);
        var byDemoted = await JobIdAsync(await SubmitAsync(demoted.Subject, Key()));
        var byRemoved = await JobIdAsync(await SubmitAsync(removed.Subject, Key()));
        var suspended = await fixture.Harness.CreateTenantAsync("Suspended exporter");
        var admin = await fixture.Harness.AddAsync(suspended, $"admin-{Guid.NewGuid():N}", "tenant-admin", TestIssuer.Issuer);
        var bySuspended = await JobIdAsync(await SubmitAsync(admin.Subject, Key(), suspended));
        await fixture.Harness.ChangeRoleAsync(S.TenantA, demoted.Id, "customer");
        await fixture.Harness.RemoveAsync(S.TenantA, removed.Id);
        await fixture.Harness.SuspendTenantAsync(suspended);

        await RunAsync();

        Assert.Equal("failed:requester_not_authorized:unwritten", await OutcomeAsync(S.TenantA, byDemoted));
        Assert.Equal("failed:requester_not_authorized:unwritten", await OutcomeAsync(S.TenantA, byRemoved));
        Assert.Equal("failed:tenant_suspended:unwritten", await OutcomeAsync(suspended, bySuspended));
    }

    // ---- helpers ----

    private static async Task<Guid> JobIdAsync(HttpResponseMessage submitted)
    {
        using (submitted)
        {
            Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
            return (await PaymentScenario.JsonAsync(submitted)).GetProperty("id").GetGuid();
        }
    }

    // "status:failure code:written or unwritten", read in the job's tenant: a suspended tenant's routes answer no one.
    private Task<string?> OutcomeAsync(Guid tenant, Guid job) => S.TextAsync(
        tenant,
        """
        SELECT j.status || ':' || coalesce(j.failure_code, '') || ':' || CASE WHEN f.size IS NULL THEN 'unwritten' ELSE 'written' END
        FROM imports_exports.export_jobs j JOIN imports_exports.stored_files f ON f.id = j.file_id WHERE j.id = @id
        """,
        job);

    private static string Key() => $"export-{Guid.NewGuid():N}";

    private Task RunAsync() => Worker.RunTimersAfterAsync(TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow);

    private async Task<Guid> CreateProductAsync(string sku, string name)
    {
        using var created = await S.SendAsync(HttpMethod.Post, $"/v1/tenants/{S.TenantA}/products", S.AdminA,
            new { sku, name, price = new { amount = 1m, currency = "EUR" } });
        return (await PaymentScenario.JsonAsync(created)).GetProperty("id").GetGuid();
    }

    private async Task<HttpResponseMessage> SubmitAsync(string subject, string? key, Guid? tenant = null)
    {
        using var client = _factory!.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/tenants/{tenant ?? S.TenantA}/exports/products");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Shared.CreateToken(subject));
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> ImportAsync(string csv)
    {
        using var client = _factory!.CreateClient();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "products.csv");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/tenants/{S.TenantA}/imports/products") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Shared.CreateToken(S.AdminA));
        request.Headers.Add("Idempotency-Key", $"import-{Guid.NewGuid():N}");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<System.Text.Json.JsonElement> StatusJsonAsync(HttpResponseMessage submitted)
    {
        using var read = await S.SendAsync(HttpMethod.Get, submitted.Headers.Location!.ToString(), S.AdminA);
        return await PaymentScenario.JsonAsync(read);
    }

    // Storage that cannot be reached, as an S3 outage looks to the module; its message stands for the provider's detail.
    private sealed class UnavailableStorage : IObjectStorage
    {
        public const string ProviderDetail = "provider-detail-that-must-never-reach-a-client";

        public Task<StoredObject> PutAsync(string key, Stream content, long maxBytes, CancellationToken cancellationToken) =>
            throw new StorageUnavailableException(ProviderDetail);

        public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken) => throw new StorageUnavailableException(ProviderDetail);

        public Task DeleteAsync(string key, CancellationToken cancellationToken) => throw new StorageUnavailableException(ProviderDetail);

        public Task<int> DeleteTemporaryFilesAsync(DateTimeOffset olderThan, CancellationToken cancellationToken) => Task.FromResult(0);
    }
}
