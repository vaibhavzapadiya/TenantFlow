using Npgsql;
using TenantFlow.Audit.Infrastructure;
using TenantFlow.BuildingBlocks.Abstractions.Workflow;
using TenantFlow.BuildingBlocks.Persistence.Retention;
using TenantFlow.Catalog.Infrastructure;
using TenantFlow.IdentityAccess.Infrastructure;
using TenantFlow.ImportsExports.Infrastructure;
using TenantFlow.Notifications.Infrastructure;
using TenantFlow.Orders.Infrastructure;
using TenantFlow.Payments.Infrastructure;
using TenantFlow.Subscriptions.Infrastructure;
using TenantFlow.Tenants.Contracts;
using TenantFlow.Tenants.Infrastructure;

namespace TenantFlow.Worker.Retention;

/// <summary>
/// The daily retention run (SPEC-0009 criteria 8 to 10, ADR-0025 decision 4). Each module owns its rules; this task pages
/// through every tenant, suspended ones included, and runs all rules for one tenant at a time under row-level security
/// (<see cref="TenantRetention"/>). It reads no tenant data outside a tenant's own scope. A tenant whose rules fail is
/// logged and skipped, so it never holds back the tenants after it; a run that stops part-way is finished by the next one.
/// </summary>
internal static partial class RetentionTask
{
    public const string Name = "retention";

    /// <summary>Every day at 03:15 UTC.</summary>
    public const string CronExpression = "0 15 3 * * ?";

    private const int TenantPage = 100;

    /// <summary>Every module's rules, in module order; within a module, in the order the module gives.</summary>
    public static IReadOnlyList<RetentionRule> Rules { get; } =
    [
        .. NotificationsRetention.Rules,
        .. OrdersRetention.Rules,
        .. PaymentsRetention.Rules,
        .. SubscriptionsRetention.Rules,
        .. CatalogRetention.Rules,
        .. ImportsExportsRetention.Rules,
        .. IdentityAccessRetention.Rules,
        .. TenantsRetention.Rules,
        .. AuditRetention.Rules,
    ];

    public static ScheduledTask Task { get; } = new(Name, CronExpression, RunAsync);

    /// <summary>Applies <see cref="Rules"/> to every tenant as of now; returns the rows changed and the tenants that failed.</summary>
    public static async Task<(long Changed, int Failed)> RunAllAsync(ITenantDirectory tenants, NpgsqlDataSource dataSource, DateTimeOffset now, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        long changed = 0;
        var failed = 0;
        Guid? after = null;
        while (await tenants.ListIdsAsync(after, TenantPage, cancellationToken) is { Count: > 0 } page)
        {
            foreach (var tenant in page)
            {
                try
                {
                    changed += await TenantRetention.RunAsync(dataSource, tenant, Rules, now, cancellationToken);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    failed++;
                    LogTenantFailed(logger, exception, tenant);
                }
            }

            after = page[^1];
        }

        return (changed, failed);
    }

    private static async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RetentionTask));
        var (changed, failed) = await RunAllAsync(
            services.GetRequiredService<ITenantDirectory>(),
            services.GetRequiredService<NpgsqlDataSource>(),
            services.GetRequiredService<TimeProvider>().GetUtcNow(),
            logger,
            cancellationToken);
        LogRun(logger, changed, failed);
    }

    // Counts only: no tenant, table or row is named.
    [LoggerMessage(EventId = 5108, Level = LogLevel.Information, Message = "Retention run changed {Rows} rows; {Failed} tenant(s) failed")]
    private static partial void LogRun(ILogger logger, long rows, int failed);

    // The tenant id is an opaque identifier, not personal data; it is what an operator needs to find the failing tenant.
    [LoggerMessage(EventId = 5109, Level = LogLevel.Error, Message = "Retention failed for tenant {TenantId}; carrying on with the next tenant")]
    private static partial void LogTenantFailed(ILogger logger, Exception exception, Guid tenantId);
}
