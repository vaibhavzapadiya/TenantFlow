namespace TenantFlow.BuildingBlocks.Abstractions.Workflow;

/// <summary>
/// A recurring task of a module that is not about one tenant's row (ADR-0023 decision 1), for example refreshing metrics.
/// The Worker's scheduler fires it on its cron schedule on one instance at a time, in a new DI scope with no tenant set, so
/// it must not read tenant-owned data. A missed fire is skipped, not made up.
/// </summary>
/// <param name="Name">A stable, unique name such as <c>imports_exports.metrics</c>.</param>
/// <param name="CronExpression">A Quartz cron expression with seconds, such as <c>0/30 * * * * ?</c>.</param>
/// <param name="Run">The work, with the scope's services.</param>
public sealed record ScheduledTask(string Name, string CronExpression, Func<IServiceProvider, CancellationToken, Task> Run);
