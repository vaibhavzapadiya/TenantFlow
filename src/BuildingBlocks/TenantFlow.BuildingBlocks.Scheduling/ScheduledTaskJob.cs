using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace TenantFlow.BuildingBlocks.Scheduling;

/// <summary>
/// The Quartz job behind every <see cref="TenantFlow.BuildingBlocks.Abstractions.Workflow.ScheduledTask"/>: finds the task
/// by the name in its job data and runs it in a new scope. One instance at a time (clustered store,
/// <see cref="DisallowConcurrentExecutionAttribute"/>); a failure is logged and the next fire runs it again. Every fire
/// that ends is recorded in <see cref="SchedulingMetrics"/> under the task's name.
/// </summary>
[DisallowConcurrentExecution]
public sealed partial class ScheduledTaskJob(SchedulerJobs jobs, IServiceScopeFactory scopes, SchedulingMetrics metrics, ILogger<ScheduledTaskJob> logger) : IJob
{
    /// <summary>The job-data key that names the task.</summary>
    public const string TaskKey = "task";

    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var name = context.MergedJobDataMap.GetString(TaskKey);
        var task = jobs.Tasks.SingleOrDefault(t => t.Name == name)
            ?? throw new JobExecutionException($"No scheduled task named '{name}'.");
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await task.Run(scope.ServiceProvider, context.CancellationToken);
            metrics.RecordRun(context, task.Name, succeeded: true, startedAt);
        }
        catch (Exception exception) when (!context.CancellationToken.IsCancellationRequested)
        {
            LogRunFailed(logger, exception, task.Name);
            metrics.RecordRun(context, task.Name, succeeded: false, startedAt);
        }
    }

    [LoggerMessage(EventId = 5208, Level = LogLevel.Error, Message = "The scheduled task {Task} failed; retrying on the next fire")]
    private static partial void LogRunFailed(ILogger logger, Exception exception, string task);
}
