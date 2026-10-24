using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Quartz;
using Quartz.Impl.Matchers;
using TenantFlow.BuildingBlocks.Abstractions.Workflow;
using TenantFlow.BuildingBlocks.Messaging.Timers;
using TenantFlow.BuildingBlocks.Scheduling;

namespace TenantFlow.Database.Tests.Messaging;

/// <summary>
/// SPEC-0007 criterion 8 (ADR-0023 decision 1) against real PostgreSQL: Quartz.NET, registered as the Worker registers it,
/// keeps its jobs in the clustered job store of the <c>scheduler</c> schema (written by the Migrator, used as the
/// application role) and its per-module job runs that module's due rows.
/// </summary>
public sealed class SchedulerTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>
{
    private static readonly string[] Schemas = ["orders", "payments", "subscriptions"];

    [Fact]
    public async Task Quartz_runs_each_modules_due_rows_from_a_clustered_persistent_job_store()
    {
        var subject = Guid.CreateVersion7();
        await InsertDueRowAsync(subject);
        var ran = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var taskRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var connection = fixture.Database.AppConnectionString;
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connection));
        foreach (var schema in Schemas)
        {
            builder.Services.AddSingleton(provider => new WorkflowTimerStore(provider.GetRequiredService<NpgsqlDataSource>(), schema));
        }

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(_ => new WorkflowTimerHandlers(static (_, _) => { }, [new("test.quartz", (_, timer, _) =>
        {
            ran.TrySetResult(timer.SubjectId);
            return Task.FromResult(TimerDecision.Complete);
        })]));
        builder.Services.AddSingleton<WorkflowTimerRunner>();
        // A module task on a cron schedule (every second here), run by the same clustered scheduler.
        ScheduledTask task = new("test.every_second", "* * * * * ?", (_, _) =>
        {
            taskRan.TrySetResult();
            return Task.CompletedTask;
        });
        builder.Services.AddTenantFlowScheduler(connection, Schemas, SchedulerRegistration.DefaultInterval, [task]);
        using var host = builder.Build();
        using var runs = new MeterCapture(SchedulingMetrics.RunsCounter);
        using var lateness = new MeterCapture(SchedulingMetrics.LatenessHistogram);
        using var dead = new MeterCapture(TimerMetrics.DeadTimersGauge);

        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var ranFor = await ran.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            await taskRan.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            var scheduler = await host.Services.GetRequiredService<ISchedulerFactory>().GetScheduler(SchedulerRegistration.SchedulerName, TestContext.Current.CancellationToken);
            var metadata = await scheduler!.GetMetaData(TestContext.Current.CancellationToken);
            var jobs = await scheduler.GetJobKeys(GroupMatcher<JobKey>.GroupEquals(SchedulerRegistration.JobGroup), TestContext.Current.CancellationToken);

            Assert.Equal(subject, ranFor);
            Assert.True(metadata.JobStoreClustered);
            Assert.True(metadata.JobStoreSupportsPersistence);
            Assert.Equal(Schemas, jobs.Select(static key => key.Name).Order(StringComparer.Ordinal));
            // The job store is the database's: the jobs and their triggers are rows of the scheduler schema.
            Assert.Equal(3L, await ScalarAsync("SELECT count(*) FROM scheduler.qrtz_triggers WHERE trigger_group = 'workflow-timers'"));
            Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM scheduler.qrtz_cron_triggers WHERE trigger_group = 'scheduled-tasks' AND trigger_name = 'test.every_second'"));

            // SPEC-0010 criterion 3: each fire is counted by job and outcome with its lateness, and each timer job then
            // reports its module's dead timers.
            await WaitUntilAsync(() =>
                runs.Measurements.Any(static m => m.Tags["job"] == "test.every_second") &&
                runs.Measurements.Any(static m => m.Tags["job"] == "timers.orders"));
            Assert.Contains(runs.Measurements, static m => m.Tags["job"] == "test.every_second" && m.Tags["outcome"] == "succeeded");
            Assert.Contains(lateness.Measurements, static m => m.Tags["job"] == "timers.orders");
            dead.RecordObservableInstruments();
            Assert.Contains(dead.Measurements, static m => m.Tags["module"] == "orders" && m.Value == 0);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The scheduler recorded no run within 30 seconds.");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_task_no_longer_in_the_code_is_removed_from_the_shared_job_store_when_a_worker_starts()
    {
        const string Orphan = "test.removed_task";
        await RunSchedulerAsync([new(Orphan, "0 0 0 1 1 ? 2099", static (_, _) => Task.CompletedTask)]);
        var storedBefore = await ScalarAsync($"SELECT count(*) FROM scheduler.qrtz_job_details WHERE job_group = 'scheduled-tasks' AND job_name = '{Orphan}'");

        await RunSchedulerAsync([]);

        Assert.Equal(1L, storedBefore);
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM scheduler.qrtz_job_details WHERE job_group = 'scheduled-tasks' AND job_name = '{Orphan}'"));
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM scheduler.qrtz_triggers WHERE trigger_group = 'scheduled-tasks' AND trigger_name = '{Orphan}'"));
    }

    // Starts a Worker-like scheduler with these tasks, waits until it has started, and stops it.
    private async Task RunSchedulerAsync(ScheduledTask[] tasks)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var connection = fixture.Database.AppConnectionString;
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connection));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(_ => new WorkflowTimerHandlers(static (_, _) => { }, []));
        builder.Services.AddSingleton<WorkflowTimerRunner>();
        builder.Services.AddTenantFlowScheduler(connection, [], SchedulerRegistration.DefaultInterval, tasks);
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var factory = host.Services.GetRequiredService<ISchedulerFactory>();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (await factory.GetScheduler(SchedulerRegistration.SchedulerName, TestContext.Current.CancellationToken) is not { IsStarted: true })
            {
                Assert.True(DateTimeOffset.UtcNow < deadline, "The scheduler did not start.");
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private async Task InsertDueRowAsync(Guid subject)
    {
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.AppConnectionString);
        await using var command = dataSource.CreateCommand(
            "INSERT INTO subscriptions.workflow_timers (id, tenant_id, kind, subject_id, due_at) VALUES (@id, @tenant, 'test.quartz', @subject, now() - interval '1 minute')");
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("tenant", Guid.CreateVersion7());
        command.Parameters.AddWithValue("subject", subject);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.AppConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
