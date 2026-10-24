using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using Quartz.Impl.Matchers;
using TenantFlow.BuildingBlocks.Abstractions.Workflow;

namespace TenantFlow.BuildingBlocks.Scheduling;

/// <summary>
/// The module schemas whose due rows Quartz runs and how often, and the modules' recurring tasks (ADR-0023 decision 1).
/// </summary>
public sealed record SchedulerJobs(IReadOnlyList<string> Schemas, TimeSpan Interval, IReadOnlyList<ScheduledTask> Tasks);

/// <summary>
/// Starts Quartz.NET in the background (ADR-0023 decision 1). The job store is the database, so the scheduler can start
/// only once PostgreSQL answers: until then the Worker stays live (its readiness reports the database) and this retries
/// every <see cref="RetryDelay"/>, instead of failing the host as Quartz's own hosted service would. Each module's job and
/// trigger are stored with <c>replace</c>, so every Worker instance may write them on start. On stop, running jobs finish.
/// </summary>
internal sealed partial class SchedulerHostedService(ISchedulerFactory factory, SchedulerJobs jobs, TimeProvider clock, ILogger<SchedulerHostedService> logger) : BackgroundService
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    private IScheduler? _scheduler;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var scheduler = await factory.GetScheduler(stoppingToken);
                // Kept before it starts: a scheduler that never started is still shut down on stop, so it leaves Quartz's
                // process-wide repository with this host.
                _scheduler = scheduler;
                foreach (var schema in jobs.Schemas)
                {
                    var job = JobBuilder.Create<WorkflowTimerJob>()
                        .WithIdentity(schema, SchedulerRegistration.JobGroup)
                        .UsingJobData(WorkflowTimerJob.SchemaKey, schema)
                        .StoreDurably()
                        .Build();
                    var trigger = TriggerBuilder.Create()
                        .ForJob(job)
                        .WithIdentity(schema, SchedulerRegistration.JobGroup)
                        .StartNow()
                        .WithSimpleSchedule(schedule => schedule.WithInterval(jobs.Interval).RepeatForever().WithMisfireHandlingInstructionFireNow())
                        .Build();
                    await scheduler.ScheduleJob(job, [trigger], replace: true, stoppingToken);
                }

                // A task removed or renamed in code leaves the shared store, so no instance keeps firing it.
                var current = jobs.Tasks.Select(static t => t.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var key in await scheduler.GetJobKeys(GroupMatcher<JobKey>.GroupEquals(SchedulerRegistration.TaskGroup), stoppingToken))
                {
                    if (!current.Contains(key.Name))
                    {
                        await scheduler.DeleteJob(key, stoppingToken);
                    }
                }

                foreach (var task in jobs.Tasks)
                {
                    var job = JobBuilder.Create<ScheduledTaskJob>()
                        .WithIdentity(task.Name, SchedulerRegistration.TaskGroup)
                        .UsingJobData(ScheduledTaskJob.TaskKey, task.Name)
                        .StoreDurably()
                        .Build();
                    // A missed fire is skipped: a recurring task catches up on its next fire.
                    var trigger = TriggerBuilder.Create()
                        .ForJob(job)
                        .WithIdentity(task.Name, SchedulerRegistration.TaskGroup)
                        .WithCronSchedule(task.CronExpression, schedule => schedule.InTimeZone(TimeZoneInfo.Utc).WithMisfireHandlingInstructionDoNothing())
                        .Build();
                    await scheduler.ScheduleJob(job, [trigger], replace: true, stoppingToken);
                }

                await scheduler.Start(stoppingToken);
                return;
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                LogStartFailed(logger, exception, RetryDelay);
            }

            try
            {
                await Task.Delay(RetryDelay, clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown before the scheduler could start.
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_scheduler is { IsShutdown: false } scheduler)
        {
            // A running batch finishes; a crash instead is covered by its rows' leases.
            await scheduler.Shutdown(waitForJobsToComplete: true, cancellationToken);
        }
    }

    public override void Dispose()
    {
        // Quartz keeps its log provider in a static: let go of this host's logger factory with the host, so a later host
        // in the same process (tests) never logs through a disposed one.
        Quartz.Logging.LogContext.SetCurrentLogProvider(NullLoggerFactory.Instance);
        base.Dispose();
    }

    [LoggerMessage(EventId = 5207, Level = LogLevel.Error, Message = "The scheduler could not start; retrying in {Delay}")]
    private static partial void LogStartFailed(ILogger logger, Exception exception, TimeSpan delay);
}
