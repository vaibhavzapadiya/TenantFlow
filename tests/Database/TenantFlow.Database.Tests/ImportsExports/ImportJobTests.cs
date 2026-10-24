using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TenantFlow.BuildingBlocks.Abstractions.Workflow;
using TenantFlow.Catalog.Contracts;
using TenantFlow.ImportsExports.Application.Abstractions;
using TenantFlow.ImportsExports.Application.ProductImports;

namespace TenantFlow.Database.Tests.ImportsExports;

/// <summary>
/// SPEC-0008 criteria 3 to 6 and 9 against real PostgreSQL: CSV product imports submitted through the real API and run by
/// the Worker's modules from their due rows (<see cref="WorkerHarness"/>), as Quartz would run them.
/// </summary>
public sealed class ImportJobTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
{
    private const string Header = "status,sku,name,price_amount,price_currency";

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

    // ---- criteria 3, 4 and 6 ----

    [Fact]
    public async Task An_import_creates_and_updates_products_by_sku_and_reports_bad_rows_without_their_values()
    {
        using var existing = await S.SendAsync(HttpMethod.Post, $"/v1/tenants/{S.TenantA}/products", S.AdminA,
            new { sku = "IMP-1", name = "Old name", price = new { amount = 1m, currency = "EUR" } });
        var csv = Csv(
            "active,imp-1,New name,2.50,EUR",
            "archived,IMP-2,Second,3.00,usd",
            "active,IMP-3,Bad price,abc,EURO",
            "active,IMP-4,Too dear,10000000000000000,EUR");

        using var submitted = await SubmitAsync(S.AdminA, csv, Key());
        var job = await PaymentScenario.JsonAsync(submitted);
        await RunAsync();
        using var read = await S.SendAsync(HttpMethod.Get, submitted.Headers.Location!.ToString(), S.AdminA);
        using var errors = await S.SendAsync(HttpMethod.Get, $"{submitted.Headers.Location}/errors", S.AdminA);
        var done = await PaymentScenario.JsonAsync(read);
        var errorText = await errors.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal((HttpStatusCode.Accepted, "queued"), (submitted.StatusCode, job.GetProperty("status").GetString()));
        Assert.Equal(("succeeded", 1, 1, 2, 4), (done.GetProperty("status").GetString(), done.GetProperty("created").GetInt32(), done.GetProperty("updated").GetInt32(),
            done.GetProperty("failed").GetInt32(), done.GetProperty("rowsProcessed").GetInt32()));
        Assert.Contains("\"column\":\"price_amount\"", errorText, StringComparison.Ordinal);
        Assert.Contains("\"column\":\"price_currency\"", errorText, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", errorText, StringComparison.Ordinal);
        Assert.DoesNotContain("EURO", errorText, StringComparison.Ordinal);
        Assert.Equal("New name", await S.TextAsync(S.TenantA, "SELECT name FROM catalog.products WHERE lower(sku) = 'imp-1' AND @id IS NOT NULL", Guid.Empty));
        Assert.Equal("archived", await S.TextAsync(S.TenantA, "SELECT status FROM catalog.products WHERE sku = 'IMP-2' AND @id IS NOT NULL", Guid.Empty));
        Assert.Equal(0, await S.CountAsync(S.TenantA, "SELECT count(*) FROM catalog.products WHERE sku = 'IMP-3' AND @id IS NOT NULL", Guid.Empty));
        // SPEC-0009 criterion 2: the request is audited once, naming the requesting member by membership id only.
        Assert.Equal(1, await S.CountAsync(S.TenantA, AuditOutbox.Count("imports_exports", "import.requested", "import_job", "SELECT requested_by FROM imports_exports.import_jobs WHERE id = @id"), job.GetProperty("id").GetGuid()));
    }

    // ---- criterion 3 ----

    [Fact]
    public async Task Submission_is_idempotent_bounded_and_for_managers_only()
    {
        var key = Key();
        using var first = await SubmitAsync(S.AdminA, Csv("active,K-1,One,1,EUR"), key);
        using var again = await SubmitAsync(S.AdminA, Csv("active,K-1,One,1,EUR"), key);
        using var otherFile = await SubmitAsync(S.AdminA, Csv("active,K-2,Two,1,EUR"), key);
        using var second = await SubmitAsync(S.AdminA, Csv("active,K-3,Three,1,EUR"), Key());
        using var third = await SubmitAsync(S.AdminA, Csv("active,K-4,Four,1,EUR"), Key());
        using var byCustomer = await SubmitAsync(S.CustomerA, Csv("active,K-5,Five,1,EUR"), Key());
        using var tooLarge = await SubmitAsync(S.AdminA, new string('x', (5 * 1024 * 1024) + 1), Key());
        using var notCsv = await SubmitAsync(S.AdminA, Csv("active,K-6,Six,1,EUR"), Key(), "application/pdf");

        Assert.Equal((HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.Accepted), (first.StatusCode, again.StatusCode, second.StatusCode));
        Assert.Equal((await PaymentScenario.JsonAsync(first)).GetProperty("id").GetGuid(), (await PaymentScenario.JsonAsync(again)).GetProperty("id").GetGuid());
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "imports_exports.idempotency_key_reused"), (otherFile.StatusCode, await PaymentScenario.CodeAsync(otherFile)));
        Assert.Equal((HttpStatusCode.Conflict, "imports_exports.too_many_jobs"), (third.StatusCode, await PaymentScenario.CodeAsync(third)));
        Assert.Equal(HttpStatusCode.Forbidden, byCustomer.StatusCode);
        Assert.Equal((HttpStatusCode.RequestEntityTooLarge, "imports_exports.file_too_large"), (tooLarge.StatusCode, await PaymentScenario.CodeAsync(tooLarge)));
        Assert.Equal(HttpStatusCode.BadRequest, notCsv.StatusCode);
    }

    [Fact]
    public async Task Only_the_upload_route_reads_more_than_the_server_wide_body_limit_and_a_bad_key_is_refused_first()
    {
        // TestServer does not enforce Kestrel's 64 KiB body limit; KestrelBodyLimit does, and lets routing raise it per route.
        await using var limited = ApiFactory.CreateProduction(
            configureServices: static services => KestrelBodyLimit.Add(services, 64 * 1024),
            connectionString: fixture.Database.AppConnectionString);
        var oneMegabyte = Csv([.. Enumerable.Range(1, 9_000).Select(static i => $"active,MB-{i},{new string('n', 100)},1,EUR")]);

        using var accepted = await SubmitAsync(limited, S.AdminA, oneMegabyte, Key());
        using var tooLarge = await SubmitAsync(limited, S.AdminA, new string('x', 6 * 1024 * 1024), Key());
        using var badKey = await SubmitAsync(limited, S.AdminA, new string('x', 6 * 1024 * 1024), "");
        using var client = limited.CreateClient();
        using var bigJson = new HttpRequestMessage(HttpMethod.Post, $"/v1/tenants/{S.TenantA}/products")
        {
            Content = new StringContent($$"""{"sku":"BIG","name":"{{new string('n', 100 * 1024)}}"}""", Encoding.UTF8, "application/json"),
        };
        bigJson.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Shared.CreateToken(S.AdminA));
        using var otherRoute = await client.SendAsync(bigJson, TestContext.Current.CancellationToken);

        Assert.True(oneMegabyte.Length > 1024 * 1024);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal((HttpStatusCode.RequestEntityTooLarge, "request.payload_too_large"), (tooLarge.StatusCode, await PaymentScenario.CodeAsync(tooLarge)));
        Assert.Equal((HttpStatusCode.BadRequest, "imports_exports.idempotency_key.invalid"), (badKey.StatusCode, await PaymentScenario.CodeAsync(badKey)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, otherRoute.StatusCode);
    }

    // ---- criterion 4 ----

    [Fact]
    public async Task A_wrong_header_too_many_rows_or_text_that_is_not_csv_fails_the_import_before_anything_is_written()
    {
        using var badHeader = await SubmitAsync(S.AdminA, "sku,name,price,currency,status\nH-1,One,1,EUR,active\n", Key());
        using var tooMany = await SubmitAsync(S.AdminA, Csv([.. Enumerable.Range(1, 10_001).Select(static i => $"active,BIG-{i},Big,1,EUR")]), Key());
        await RunAsync();
        using var notCsv = await SubmitAsync(S.AdminA, Csv("active,Q-1,One,1,EUR", "active,Q-2,\"unclosed,1,EUR"), Key());
        await RunAsync();

        Assert.Equal(("failed", "invalid_header"), await StatusAsync(badHeader));
        Assert.Equal(("failed", "too_many_rows"), await StatusAsync(tooMany));
        Assert.Equal(("failed", "file_unreadable"), await StatusAsync(notCsv));
        Assert.Equal(0, await S.CountAsync(S.TenantA, "SELECT count(*) FROM catalog.products WHERE (sku = 'H-1' OR sku LIKE 'BIG-%' OR sku LIKE 'Q-%') AND @id IS NOT NULL", Guid.Empty));
    }

    // ---- criterion 5 ----

    [Fact]
    public async Task An_import_that_crashed_after_a_batch_resumes_without_creating_any_product_twice()
    {
        var rows = Enumerable.Range(1, 1_200).Select(static i => $"active,RES-{i},Resumed {i},1.00,EUR").ToArray();
        using var submitted = await SubmitAsync(S.AdminA, Csv(rows), Key());
        var jobId = (await PaymentScenario.JsonAsync(submitted)).GetProperty("id").GetGuid();

        // A run applied the first batch in Catalog and crashed before its checkpoint.
        var applied = await Worker.InTenantAsync(S.TenantA, services => services.GetRequiredService<ICatalogImport>().ApplyBatchAsync(jobId, 1,
            [.. Enumerable.Range(1, 500).Select(static i => new ProductImportRow(i, $"RES-{i}", $"Resumed {i}", "1.00", "EUR", "active"))], TestContext.Current.CancellationToken));
        await RunAsync();

        Assert.Equal(500, applied.Created);
        var done = await StatusJsonAsync(submitted);
        Assert.Equal(("succeeded", 1_200, 0, 1_200), (done.GetProperty("status").GetString(), done.GetProperty("created").GetInt32(), done.GetProperty("updated").GetInt32(),
            done.GetProperty("rowsProcessed").GetInt32()));
        Assert.Equal(1_200, await S.CountAsync(S.TenantA, "SELECT count(*) FROM catalog.products WHERE sku LIKE 'RES-%' AND @id IS NOT NULL", Guid.Empty));
    }

    [Fact]
    public async Task A_checkpoint_only_moves_from_the_value_the_run_read()
    {
        using var submitted = await SubmitAsync(S.AdminA, Csv("active,CP-1,One,1,EUR"), Key());
        var jobId = (await PaymentScenario.JsonAsync(submitted)).GetProperty("id").GetGuid();
        var now = DateTimeOffset.UtcNow;
        var empty = new ImportBatchResult(0, 0, []);

        // Two runs read checkpoint 0; the first moves it, so the second must not.
        var (started, first, second) = await Worker.InTenantAsync(S.TenantA, async services =>
        {
            var store = services.GetRequiredService<IImportJobStore>();
            var ct = TestContext.Current.CancellationToken;
            return (await store.StartRunningAsync(jobId, now, ct), await store.CheckpointAsync(jobId, 0, 500, empty, [], now, ct),
                await store.CheckpointAsync(jobId, 0, 500, empty, [], now, ct));
        });

        Assert.Equal((true, true, false), (started, first, second));
    }

    // ---- criterion 9 ----

    [Fact]
    public async Task Another_tenant_can_neither_read_an_import_nor_its_errors()
    {
        using var submitted = await SubmitAsync(S.AdminA, Csv("active,X-1,One,abc,EUR"), Key());
        var jobId = (await PaymentScenario.JsonAsync(submitted)).GetProperty("id").GetGuid();
        await RunAsync();

        using var read = await S.SendAsync(HttpMethod.Get, $"/v1/tenants/{S.TenantB}/imports/{jobId}", S.AdminB);
        using var errors = await S.SendAsync(HttpMethod.Get, $"/v1/tenants/{S.TenantB}/imports/{jobId}/errors", S.AdminB);

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (read.StatusCode, errors.StatusCode));
        Assert.Equal(("imports_exports.job_not_found", "imports_exports.job_not_found"), (await PaymentScenario.CodeAsync(read), await PaymentScenario.CodeAsync(errors)));
    }

    // ---- SPEC-0009 criterion 11 ----

    [Fact]
    public async Task An_import_stops_before_its_next_batch_once_its_requester_is_demoted_and_keeps_the_batches_applied()
    {
        var manager = await fixture.Harness.AddAsync(S.TenantA, $"importer-{Guid.NewGuid():N}", "tenant-admin", TestIssuer.Issuer);
        var prefix = $"L10{Guid.NewGuid():N}"[..14];
        var perRun = ImportLimits.BatchSize * ImportLimits.BatchesPerRun;
        using var submitted = await SubmitAsync(manager.Subject, Csv([.. Enumerable.Range(1, perRun + 1).Select(i => $"active,{prefix}-{i},Item {i},1.00,EUR")]), Key());
        var jobId = (await PaymentScenario.JsonAsync(submitted)).GetProperty("id").GetGuid();

        // One run applies its batches and asks to run again, as a timer would see it; then the manager is demoted.
        var first = await Worker.InTenantAsync(S.TenantA, services => services.GetRequiredService<ImportRunner>().RunAsync(jobId, TestContext.Current.CancellationToken));
        await fixture.Harness.ChangeRoleAsync(S.TenantA, manager.Id, "customer");
        var second = await Worker.InTenantAsync(S.TenantA, services => services.GetRequiredService<ImportRunner>().RunAsync(jobId, TestContext.Current.CancellationToken));

        Assert.Equal((true, TimerDecision.Complete), (first.RunAgainAt is not null, second));
        Assert.Equal(("failed", "requester_not_authorized"), await StatusAsync(submitted));
        Assert.Equal(perRun, await S.CountAsync(S.TenantA, $"SELECT count(*) FROM catalog.products WHERE sku LIKE '{prefix}-%' AND @id IS NOT NULL", Guid.Empty));
    }

    [Fact]
    public async Task A_queued_import_ends_unapplied_when_its_requester_is_removed()
    {
        var manager = await fixture.Harness.AddAsync(S.TenantA, $"importer-{Guid.NewGuid():N}", "tenant-admin", TestIssuer.Issuer);
        var prefix = $"L10{Guid.NewGuid():N}"[..14];
        using var submitted = await SubmitAsync(manager.Subject, Csv($"active,{prefix}-1,Item,1.00,EUR"), Key());
        await fixture.Harness.RemoveAsync(S.TenantA, manager.Id);

        await RunAsync();

        Assert.Equal(("failed", "requester_not_authorized"), await StatusAsync(submitted));
        Assert.Equal(0, await S.CountAsync(S.TenantA, $"SELECT count(*) FROM catalog.products WHERE sku LIKE '{prefix}-%' AND @id IS NOT NULL", Guid.Empty));
    }

    // ---- helpers ----

    private static string Key() => $"import-{Guid.NewGuid():N}";

    private static string Csv(params string[] rows) => Header + "\n" + string.Join("\n", rows) + "\n";

    private Task RunAsync() => Worker.RunTimersAfterAsync(TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow);

    private Task<HttpResponseMessage> SubmitAsync(string subject, string csv, string key, string contentType = "text/csv") =>
        SubmitAsync(_factory!, subject, csv, key, contentType);

    private async Task<HttpResponseMessage> SubmitAsync(WebApplicationFactory<Program> factory, string subject, string csv, string key, string contentType = "text/csv")
    {
        using var client = factory.CreateClient();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", "products.csv");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/tenants/{S.TenantA}/imports/products") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Shared.CreateToken(subject));
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<System.Text.Json.JsonElement> StatusJsonAsync(HttpResponseMessage submitted)
    {
        using var read = await S.SendAsync(HttpMethod.Get, submitted.Headers.Location!.ToString(), S.AdminA);
        return await PaymentScenario.JsonAsync(read);
    }

    private async Task<(string?, string?)> StatusAsync(HttpResponseMessage submitted)
    {
        var job = await StatusJsonAsync(submitted);
        return (job.GetProperty("status").GetString(), job.GetProperty("failureCode").GetString());
    }
}
