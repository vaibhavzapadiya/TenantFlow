using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TenantFlow.ImportsExports.Application.Abstractions;
using TenantFlow.ImportsExports.Application.Files;
using TenantFlow.ImportsExports.Application.Observability;
using TenantFlow.ImportsExports.Domain.Files;
using TenantFlow.ImportsExports.Domain.Jobs;
using TenantFlow.ImportsExports.Infrastructure;

namespace TenantFlow.Database.Tests.ImportsExports;

/// <summary>
/// SPEC-0008 criteria 10 and 11 against real PostgreSQL: files expire through due rows, abandoned uploads fail, and the
/// metrics task and the runners report what happens, run by the Worker's modules as Quartz would run them.
/// </summary>
public sealed class FileLifecycleTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
{
    private static readonly string[] LabelNames = ["kind", "outcome"];

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

    // ---- criterion 10 ----

    [Fact]
    public async Task An_exports_file_and_a_finished_imports_upload_are_deleted_seven_days_later_and_not_before()
    {
        using var export = await ExportAsync();
        using var import = await ImportAsync("sku,name,price_amount,price_currency,status\nLIFE-1,One,1,EUR,active\n");
        var now = DateTimeOffset.UtcNow;
        await Worker.RunTimersAfterAsync(TimeSpan.FromSeconds(1), now);
        var exportFile = await FileIdAsync("export_jobs", export);
        var importFile = await FileIdAsync("import_jobs", import);

        await Worker.RunTimersAfterAsync(TimeSpan.FromDays(6), now);
        var keptAfterSixDays = (await DeletedAsync(exportFile), await DeletedAsync(importFile), await StoredAsync(exportFile), await StoredAsync(importFile));
        await Worker.RunTimersAfterAsync(TimeSpan.FromDays(8), now);
        using var download = await S.SendAsync(HttpMethod.Get, $"{export.Headers.Location}/file", S.AdminA);

        Assert.Equal((false, false, true, true), keptAfterSixDays);
        Assert.Equal((true, true, false, false), (await DeletedAsync(exportFile), await DeletedAsync(importFile), await StoredAsync(exportFile), await StoredAsync(importFile)));
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
    }

    [Fact]
    public async Task An_upload_still_receiving_after_an_hour_fails_and_its_file_is_deleted()
    {
        // A submission that committed its job and wrote its file, then crashed before queueing it.
        var now = DateTimeOffset.UtcNow;
        var (jobId, fileId) = await Worker.InTenantAsync(S.TenantA, async services =>
        {
            var ct = TestContext.Current.CancellationToken;
            var file = StoredFile.Reserve(Guid.CreateVersion7(), S.TenantA, FileKinds.ImportUpload, "text/csv", now);
            var job = ImportJob.Receive(Guid.CreateVersion7(), S.TenantA, file.Id, $"lost-{Guid.NewGuid():N}", new string('0', 64),
                Guid.CreateVersion7(), now);
            Assert.Equal(JobStart.Started, await services.GetRequiredService<IImportJobStore>().TryStartAsync(job, file, 2, now + TimeSpan.FromHours(1), TenantFlow.BuildingBlocks.Abstractions.Messaging.OutgoingMessage.From(TenantFlow.Audit.Contracts.Events.AuditRecordedV1.Of(TenantFlow.Audit.Contracts.AuditActions.ImportRequested, TenantFlow.Audit.Contracts.AuditActor.Member(Guid.CreateVersion7()), TenantFlow.Audit.Contracts.AuditTargetTypes.ImportJob, job.Id, now)), ct));
            await services.GetRequiredService<StoredFiles>().WriteAsync(file, new MemoryStream("sku\n"u8.ToArray()), 100, ct);
            return (job.Id, file.Id);
        });

        await Worker.RunTimersAfterAsync(TimeSpan.FromMinutes(59), now);
        var receiving = await S.TextAsync(S.TenantA, "SELECT status FROM imports_exports.import_jobs WHERE id = @id", jobId);
        await Worker.RunTimersAfterAsync(TimeSpan.FromMinutes(61), now);

        Assert.Equal("receiving", receiving);
        Assert.Equal("failed", await S.TextAsync(S.TenantA, "SELECT status FROM imports_exports.import_jobs WHERE id = @id", jobId));
        Assert.Equal("upload_abandoned", await S.TextAsync(S.TenantA, "SELECT failure_code FROM imports_exports.import_jobs WHERE id = @id", jobId));
        Assert.Equal((true, false), (await DeletedAsync(fileId), await StoredAsync(fileId)));
    }

    // ---- criterion 11 ----

    [Fact]
    public async Task The_metrics_task_reports_due_jobs_per_kind_and_the_runners_count_each_job_that_ends()
    {
        using var due = new MeterCapture(ImportsExportsMetrics.DueJobsGauge);
        using var age = new MeterCapture(ImportsExportsMetrics.OldestDueAgeGauge);
        using var jobs = new MeterCapture(ImportsExportsMetrics.JobsCounter);
        using var failedRows = new MeterCapture(ImportsExportsMetrics.FailedRowsCounter);
        using var duration = new MeterCapture(ImportsExportsMetrics.JobDurationHistogram);
        using var export = await ExportAsync();
        using var import = await ImportAsync("sku,name,price_amount,price_currency,status\nMET-1,One,1,EUR,active\nMET-2,Two,abc,EUR,active\n");
        var now = DateTimeOffset.UtcNow;

        // A minute later, before the Worker ran them: the task, as Quartz fires it, in a scope with no tenant.
        Worker.Clock.SetUtcNow(now + TimeSpan.FromMinutes(1));
        var task = ImportsExportsModule.ScheduledTasks.Single(static t => t.Name == "imports_exports.metrics");
        await using (var scope = Worker.Services.CreateAsyncScope())
        {
            await task.Run(scope.ServiceProvider, TestContext.Current.CancellationToken);
        }

        due.RecordObservableInstruments();
        age.RecordObservableInstruments();
        jobs.Clear();
        failedRows.Clear();
        duration.Clear();
        await Worker.RunTimersAfterAsync(TimeSpan.FromMinutes(2), now);

        Assert.Equal(("imports_exports.metrics", "0/30 * * * * ?"), (task.Name, task.CronExpression));
        Assert.Equal(["imports_exports.abandon_import", "imports_exports.expire_file", "imports_exports.run_export", "imports_exports.run_import"],
            due.Measurements.Select(static m => m.Tags["kind"]).Order(StringComparer.Ordinal));
        Assert.True(due.Measurements.Single(static m => m.Tags["kind"] == "imports_exports.run_export").Value >= 1);
        Assert.True(due.Measurements.Single(static m => m.Tags["kind"] == "imports_exports.run_import").Value >= 1);
        Assert.True(age.Measurements.Single(static m => m.Tags["kind"] == "imports_exports.run_export").Value >= 59);
        Assert.Contains(jobs.Measurements, static m => m.Value == 1 && m.Tags["kind"] == "export" && m.Tags["outcome"] == "succeeded");
        Assert.Contains(jobs.Measurements, static m => m.Value == 1 && m.Tags["kind"] == "import" && m.Tags["outcome"] == "succeeded");
        Assert.Contains(failedRows.Measurements, static m => m.Value == 1 && m.Tags["kind"] == "import");
        Assert.Equal(jobs.Measurements.Count, duration.Measurements.Count);
        // Labels are fixed names: never a tenant, job or file id.
        Assert.All([.. due.Measurements, .. jobs.Measurements, .. failedRows.Measurements, .. duration.Measurements],
            static m => Assert.All(m.Tags.Keys, static key => Assert.Contains(key, LabelNames)));
    }

    // ---- helpers ----

    private async Task<HttpResponseMessage> ExportAsync()
    {
        using var client = _factory!.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/tenants/{S.TenantA}/exports/products");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Shared.CreateToken(S.AdminA));
        request.Headers.Add("Idempotency-Key", $"export-{Guid.NewGuid():N}");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return response;
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
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return response;
    }

    private async Task<Guid> FileIdAsync(string table, HttpResponseMessage submitted)
    {
        var jobId = (await PaymentScenario.JsonAsync(submitted)).GetProperty("id").GetGuid();
        return Guid.Parse((await S.TextAsync(S.TenantA, $"SELECT file_id::text FROM imports_exports.{table} WHERE id = @id", jobId))!);
    }

    private async Task<bool> DeletedAsync(Guid fileId) =>
        await S.CountAsync(S.TenantA, "SELECT count(*) FROM imports_exports.stored_files WHERE id = @id AND deleted_at IS NOT NULL", fileId) == 1;

    private async Task<bool> StoredAsync(Guid fileId) => await Worker.InTenantAsync(S.TenantA, async services =>
    {
        var kind = await S.TextAsync(S.TenantA, "SELECT kind FROM imports_exports.stored_files WHERE id = @id", fileId);
        await using var content = await services.GetRequiredService<IObjectStorage>().OpenReadAsync(StorageKeys.For(S.TenantA, kind!, fileId), TestContext.Current.CancellationToken);
        return content is not null;
    });
}
