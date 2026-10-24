using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Quartz;
using TenantFlow.BuildingBlocks.Messaging.Timers;

namespace TenantFlow.BuildingBlocks.Scheduling;

/// <summary>
/// The recurring Quartz job of one module (ADR-0023 decision 1): runs that module's due rows batch after batch until none is
/// due or <see cref="MaxBatches"/> ran, then waits for its next fire. Quartz runs it on one Worker instance at a time
/// (clustered store, <see cref="DisallowConcurrentExecutionAttribute"/>); the rows' leases still guard each row, so even
/// two runs at once could not run one row twice. A failed batch is logged; the next fire tries again. Each fire is recorded
/// in <see cref="SchedulingMetrics"/> as <c>timers.&lt;schema&gt;</c>, and then the module's dead timers are counted for
/// <see cref="TimerMetrics"/> (SPEC-0010 criterion 3).
/// </summary>
[DisallowConcurrentExecution]
public sealed partial class WorkflowTimerJob(WorkflowTimerRunner runner, SchedulingMetrics metrics, TimerMetrics timers, ILogger<WorkflowTimerJob> logger) : IJob
{
    /// <summary>The job-data key that names the module schema whose rows the job runs.</summary>
    public const string SchemaKey = "schema";

    /// <summary>Keeps one fire short, so shutdown and the next module's job are not held up by a long backlog.</summary>
    public const int MaxBatches = 50;

    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var schema = context.MergedJobDataMap.GetString(SchemaKey);
        var store = runner.Stores.SingleOrDefault(s => s.Module == schema)
            ?? throw new JobExecutionException($"No workflow timer store for schema '{schema}'.");
        var job = SchedulingMetrics.TimerJob(store.Module);
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            for (var batch = 0; batch < MaxBatches && await runner.RunDueAsync(store, context.CancellationToken) > 0; batch++)
            {
            }

            timers.RecordDead(store.Module, await store.CountDeadAsync(context.CancellationToken));
            metrics.RecordRun(context, job, succeeded: true, startedAt);
        }
        catch (Exception exception) when (!context.CancellationToken.IsCancellationRequested)
        {
            LogRunFailed(logger, exception, store.Module);
            metrics.RecordRun(context, job, succeeded: false, startedAt);
        }
    }

    [LoggerMessage(EventId = 5204, Level = LogLevel.Error, Message = "Running the due rows of {Module} failed; retrying on the next fire")]
    private static partial void LogRunFailed(ILogger logger, Exception exception, string module);
}
