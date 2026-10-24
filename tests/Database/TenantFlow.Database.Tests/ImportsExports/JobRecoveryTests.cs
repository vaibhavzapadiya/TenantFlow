using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TenantFlow.BuildingBlocks.Messaging.Timers;
using TenantFlow.ImportsExports.Application.Abstractions;
using TenantFlow.ImportsExports.Application.ProductImports;
using TenantFlow.ImportsExports.Domain.Files;
using TenantFlow.ImportsExports.Infrastructure;

namespace TenantFlow.Database.Tests.ImportsExports;

/// <summary>
/// SPEC-0008 failure modes against real PostgreSQL: a job that cannot succeed ends and frees its place, a client's retry
/// resumes an upload lost after its intent committed, and abandoning never touches a job that was queued meanwhile.
/// </summary>
public sealed class JobRecoveryTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
{
    private const string Header = "sku,name,price_amount,price_currency,status\n";

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

    [Fact]
    public async Task An_import_whose_file_is_gone_or_altered_fails_at_once_and_frees_its_place()
    {
        using var gone = await ImportAsync(Header + "GONE-1,One,1,EUR,active\n", Key());
        using var altered = await ImportAsync(Header + "ALT-1,One,1,EUR,active\n", Key());
        var goneFile = await FileIdAsync("import_jobs", gone);
        var alteredFile = await FileIdAsync("import_jobs", altered);
        await Worker.InTenantAsync(S.TenantA, async services =>
        {
            var storage = services.GetRequiredService<IObjectStorage>();
            await storage.DeleteAsync(StorageKeys.For(S.TenantA, FileKinds.ImportUpload, goneFile), TestContext.Current.CancellationToken);
            return await storage.PutAsync(StorageKeys.For(S.TenantA, FileKinds.ImportUpload, alteredFile), new MemoryStream("other"u8.ToArray()), 100, TestContext.Current.CancellationToken);
        });

        await RunAsync();
        using var next = await ImportAsync(Header + "NEXT-1,One,1,EUR,active\n", Key());

        Assert.Equal(("failed", "file_missing"), await StatusAsync("import_jobs", gone));
        Assert.Equal(("failed", "file_corrupt"), await StatusAsync("import_jobs", altered));
        Assert.Equal(HttpStatusCode.Accepted, next.StatusCode);
    }

    [Fact]
    public async Task A_job_run_that_fails_on_its_last_allowed_attempt_ends_as_run_failed()
    {
        using var export = await ExportAsync();
        var jobId = (await PaymentScenario.JsonAsync(export)).GetProperty("id").GetGuid();
        var fileId = await FileIdAsync("export_jobs", export);
        // The export's file row is gone, so every run throws.
        await S.ScalarAsync(S.TenantA, "DELETE FROM imports_exports.stored_files WHERE id = @id RETURNING id", fileId);
        var handler = ImportsExportsModule.TimerHandlers.Single(static h => h.Key == "imports_exports.run_export").Value;

        var early = await Assert.ThrowsAnyAsync<Exception>(() => RunHandlerAsync(handler, jobId, failures: 0));
        var statusAfterEarly = await StatusAsync("export_jobs", export);
        var last = await Assert.ThrowsAnyAsync<Exception>(() => RunHandlerAsync(handler, jobId, failures: WorkflowTimerRunner.MaxFailures - 1));

        Assert.Equal(("running", null), statusAfterEarly);
        Assert.Equal(early.GetType(), last.GetType());
        Assert.Equal(("failed", "run_failed"), await StatusAsync("export_jobs", export));
    }

    [Fact]
    public async Task Active_jobs_untouched_for_six_hours_no_longer_count_toward_the_limit()
    {
        using var first = await ImportAsync(Header + "STALE-1,One,1,EUR,active\n", Key());
        using var second = await ImportAsync(Header + "STALE-2,One,1,EUR,active\n", Key());
        using var refused = await ImportAsync(Header + "STALE-3,One,1,EUR,active\n", Key());
        foreach (var job in new[] { first, second })
        {
            var jobId = (await PaymentScenario.JsonAsync(job)).GetProperty("id").GetGuid();
            await S.ScalarAsync(S.TenantA, "UPDATE imports_exports.import_jobs SET updated_at = now() - interval '7 hours' WHERE id = @id RETURNING id", jobId);
        }

        using var accepted = await ImportAsync(Header + "STALE-4,One,1,EUR,active\n", Key());

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
    }

    [Fact]
    public async Task A_retry_of_an_upload_lost_after_its_intent_committed_writes_it_again_and_queues_the_job()
    {
        var key = Key();
        const string csv = Header + "LOST-1,One,1,EUR,active\n";
        using var first = await ImportAsync(csv, key);
        var jobId = (await PaymentScenario.JsonAsync(first)).GetProperty("id").GetGuid();
        var fileId = await FileIdAsync("import_jobs", first);
        // As if writing the upload had failed after the intent commit: the job receiving, no object, no size or checksum.
        await S.ScalarAsync(S.TenantA, "UPDATE imports_exports.import_jobs SET status = 'receiving' WHERE id = @id RETURNING id", jobId);
        await S.ScalarAsync(S.TenantA, "UPDATE imports_exports.stored_files SET size = NULL, sha256 = NULL WHERE id = @id RETURNING id", fileId);
        await Worker.InTenantAsync(S.TenantA, async services =>
        {
            await services.GetRequiredService<IObjectStorage>().DeleteAsync(StorageKeys.For(S.TenantA, FileKinds.ImportUpload, fileId), TestContext.Current.CancellationToken);
            return true;
        });

        using var retry = await ImportAsync(csv, key);
        var retried = await PaymentScenario.JsonAsync(retry);
        await RunAsync();

        Assert.Equal((jobId, "queued"), (retried.GetProperty("id").GetGuid(), retried.GetProperty("status").GetString()));
        Assert.Equal(("succeeded", null), await StatusAsync("import_jobs", first));
        Assert.Equal(1, await S.CountAsync(S.TenantA, "SELECT count(*) FROM catalog.products WHERE sku = 'LOST-1' AND @id IS NOT NULL", Guid.Empty));
    }

    [Fact]
    public async Task Abandoning_leaves_a_job_that_was_queued_meanwhile_and_its_file_alone()
    {
        // The abandon due row read the job while it was receiving; the upload then finished and queued it.
        var now = DateTimeOffset.UtcNow;
        var (jobId, fileId) = await Worker.InTenantAsync(S.TenantA, async services =>
        {
            var ct = TestContext.Current.CancellationToken;
            var store = services.GetRequiredService<IImportJobStore>();
            var file = StoredFile.Reserve(Guid.CreateVersion7(), S.TenantA, FileKinds.ImportUpload, "text/csv", now);
            var job = TenantFlow.ImportsExports.Domain.Jobs.ImportJob.Receive(Guid.CreateVersion7(), S.TenantA, file.Id, Key(), new string('0', 64), Guid.CreateVersion7(), now);
            Assert.Equal(JobStart.Started, await store.TryStartAsync(job, file, 2, now + TimeSpan.FromHours(1), TenantFlow.BuildingBlocks.Abstractions.Messaging.OutgoingMessage.From(TenantFlow.Audit.Contracts.Events.AuditRecordedV1.Of(TenantFlow.Audit.Contracts.AuditActions.ImportRequested, TenantFlow.Audit.Contracts.AuditActor.Member(Guid.CreateVersion7()), TenantFlow.Audit.Contracts.AuditTargetTypes.ImportJob, job.Id, now)), ct));
            await services.GetRequiredService<TenantFlow.ImportsExports.Application.Files.StoredFiles>().WriteAsync(file, new MemoryStream("sku\n"u8.ToArray()), 100, ct);
            Assert.True(await store.QueueAsync(job.Id, now, ct));
            return (job.Id, file.Id);
        });

        await Worker.InTenantAsync(S.TenantA, services => services.GetRequiredService<ImportRunner>().AbandonAsync(jobId, TestContext.Current.CancellationToken));
        // The race itself: an abandon that read "receiving" just before the job was queued must not end it.
        var endedFromQueued = await Worker.InTenantAsync(S.TenantA, services => services.GetRequiredService<IImportJobStore>().FinishAsync(
            jobId, TenantFlow.ImportsExports.Domain.Jobs.JobStatus.Failed, "upload_abandoned", now, TestContext.Current.CancellationToken, onlyFrom: TenantFlow.ImportsExports.Domain.Jobs.JobStatus.Receiving));

        Assert.False(endedFromQueued);

        Assert.Equal("queued", await S.TextAsync(S.TenantA, "SELECT status FROM imports_exports.import_jobs WHERE id = @id", jobId));
        Assert.Equal(0, await S.CountAsync(S.TenantA, "SELECT count(*) FROM imports_exports.stored_files WHERE id = @id AND deleted_at IS NOT NULL", fileId));
    }

    [Fact]
    public async Task A_due_row_of_another_tenant_never_acts_on_this_tenants_job_or_file()
    {
        using var import = await ImportAsync(Header + "CROSS-1,One,1,EUR,active\n", Key());
        using var export = await ExportAsync();
        var importId = (await PaymentScenario.JsonAsync(import)).GetProperty("id").GetGuid();
        var exportId = (await PaymentScenario.JsonAsync(export)).GetProperty("id").GetGuid();
        var importFile = await FileIdAsync("import_jobs", import);
        // Tenant A's own due rows give way to rows that carry tenant B's id but name A's job and file.
        await using (var dataSource = Npgsql.NpgsqlDataSource.Create(fixture.Database.AppConnectionString))
        {
            await using (var delete = dataSource.CreateCommand("DELETE FROM imports_exports.workflow_timers WHERE subject_id IN ($1, $2)"))
            {
                delete.Parameters.Add(new Npgsql.NpgsqlParameter { Value = importId });
                delete.Parameters.Add(new Npgsql.NpgsqlParameter { Value = exportId });
                await delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            foreach (var (kind, subject) in new[] { ("imports_exports.run_import", importId), ("imports_exports.abandon_import", importId), ("imports_exports.run_export", exportId), ("imports_exports.expire_file", importFile) })
            {
                await using var insert = dataSource.CreateCommand(
                    "INSERT INTO imports_exports.workflow_timers (id, tenant_id, kind, subject_id, due_at) VALUES ($1, $2, $3, $4, now() - interval '1 minute')");
                insert.Parameters.Add(new Npgsql.NpgsqlParameter { Value = Guid.CreateVersion7() });
                insert.Parameters.Add(new Npgsql.NpgsqlParameter { Value = S.TenantB });
                insert.Parameters.Add(new Npgsql.NpgsqlParameter { Value = kind });
                insert.Parameters.Add(new Npgsql.NpgsqlParameter { Value = subject });
                await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
        }

        await RunAsync();

        Assert.Equal(("queued", null), await StatusAsync("import_jobs", import));
        Assert.Equal(("queued", null), await StatusAsync("export_jobs", export));
        Assert.Equal(0, await S.CountAsync(S.TenantA, "SELECT count(*) FROM imports_exports.stored_files WHERE id = @id AND deleted_at IS NOT NULL", importFile));
        Assert.Equal(0, await S.CountAsync(S.TenantA, "SELECT count(*) FROM catalog.products WHERE sku = 'CROSS-1' AND @id IS NOT NULL", Guid.Empty));
    }

    // ---- helpers ----

    private static string Key() => $"recovery-{Guid.NewGuid():N}";

    private Task RunAsync() => Worker.RunTimersAfterAsync(TimeSpan.FromSeconds(5), DateTimeOffset.UtcNow);

    private Task<bool> RunHandlerAsync(TimerHandler handler, Guid jobId, int failures) =>
        Worker.InTenantAsync(S.TenantA, async services =>
        {
            var timer = new DueTimer(Guid.CreateVersion7(), S.TenantA, "imports_exports.run_export", jobId, failures, DateTimeOffset.UtcNow.AddMinutes(2), failures);
            await handler(services, timer, TestContext.Current.CancellationToken);
            return true;
        });

    private async Task<HttpResponseMessage> ImportAsync(string csv, string key)
    {
        using var client = _factory!.CreateClient();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "products.csv");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/tenants/{S.TenantA}/imports/products") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Shared.CreateToken(S.AdminA));
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> ExportAsync()
    {
        using var client = _factory!.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/tenants/{S.TenantA}/exports/products");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Shared.CreateToken(S.AdminA));
        request.Headers.Add("Idempotency-Key", Key());
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<Guid> FileIdAsync(string table, HttpResponseMessage submitted)
    {
        var jobId = (await PaymentScenario.JsonAsync(submitted)).GetProperty("id").GetGuid();
        return Guid.Parse((await S.TextAsync(S.TenantA, $"SELECT file_id::text FROM imports_exports.{table} WHERE id = @id", jobId))!);
    }

    private async Task<(string?, string?)> StatusAsync(string table, HttpResponseMessage submitted)
    {
        var jobId = (await PaymentScenario.JsonAsync(submitted)).GetProperty("id").GetGuid();
        return (await S.TextAsync(S.TenantA, $"SELECT status FROM imports_exports.{table} WHERE id = @id", jobId),
            await S.TextAsync(S.TenantA, $"SELECT failure_code FROM imports_exports.{table} WHERE id = @id", jobId));
    }
}
