using Microsoft.Extensions.DependencyInjection;
using Quartz;
using TenantFlow.BuildingBlocks.Abstractions.Workflow;
using TenantFlow.BuildingBlocks.Messaging.Timers;
using TenantFlow.BuildingBlocks.Scheduling.Persistence;

namespace TenantFlow.BuildingBlocks.Scheduling;

/// <summary>
/// Quartz.NET for the Worker (ADR-0023 decision 1): a persistent, clustered job store in the <c>scheduler</c> schema, and
/// one recurring <see cref="WorkflowTimerJob"/> per module schema and one <see cref="ScheduledTaskJob"/> per module task, scheduled
/// and started by <see cref="SchedulerHostedService"/>.
/// Clustering makes a fire run on one instance only and lets another instance take over a crashed one's jobs; a fire missed
/// while no Worker ran is made once, at once.
/// </summary>
public static class SchedulerRegistration
{
    public const string SchedulerName = "tenantflow";
    public const string TablePrefix = SchedulerDbContext.Schema + ".qrtz_";
    public const string JobGroup = "workflow-timers";
    public const string TaskGroup = "scheduled-tasks";

    /// <summary>How often each module's due rows are looked at (ADR-0023: every minute).</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

    /// <param name="services">The host's services; they must also hold the <c>WorkflowTimerRunner</c> and its stores.</param>
    /// <param name="connectionString">The application role's connection string.</param>
    /// <param name="schemas">The module schemas whose due rows are run, one job each.</param>
    /// <param name="interval">How often each job fires.</param>
    /// <param name="tasks">The modules' recurring tasks, each on its own cron schedule.</param>
    public static IServiceCollection AddTenantFlowScheduler(this IServiceCollection services, string connectionString, IEnumerable<string> schemas, TimeSpan interval, IEnumerable<ScheduledTask>? tasks = null)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        services.AddQuartz(quartz =>
        {
            quartz.SchedulerName = SchedulerName;
            quartz.SchedulerId = "AUTO";
            quartz.UsePersistentStore(store =>
            {
                // Job data as strings only, so nothing in the store needs binary deserialization.
                store.UseProperties = true;
                // Validation would connect while the scheduler is created, before the database may be up; the Migrator owns
                // the schema and its tests check it (SchedulerHostedService starts Quartz once PostgreSQL answers).
                store.PerformSchemaValidation = false;
                store.UseClustering();
                store.UsePostgres(postgres =>
                {
                    postgres.ConnectionString = connectionString;
                    postgres.TablePrefix = TablePrefix;
                });
                store.UseSystemTextJsonSerializer();
            });
        });
        services.AddSingleton(new SchedulerJobs([.. schemas], interval, [.. tasks ?? []]));
        services.AddSingleton<SchedulingMetrics>();
        services.AddSingleton<TimerMetrics>();
        services.AddHostedService<SchedulerHostedService>();
        return services;
    }
}
