using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TenantFlow.Audit.Infrastructure.Persistence;
using TenantFlow.BuildingBlocks.Scheduling.Persistence;
using TenantFlow.Catalog.Infrastructure.Persistence;
using TenantFlow.IdentityAccess.Infrastructure.Persistence;
using TenantFlow.ImportsExports.Infrastructure.Persistence;
using TenantFlow.Inventory.Infrastructure.Persistence;
using TenantFlow.Notifications.Infrastructure.Persistence;
using TenantFlow.Operations.Infrastructure.Persistence;
using TenantFlow.Orders.Infrastructure.Persistence;
using TenantFlow.Payments.Infrastructure.Persistence;
using TenantFlow.Subscriptions.Infrastructure.Persistence;
using TenantFlow.Tenants.Infrastructure.Persistence;

namespace TenantFlow.Migrator;

/// <summary>Runs the Migrator and returns its process exit code: 0 on success, 1 on failure.</summary>
public static partial class MigratorApplication
{
    /// <summary>The owner role's connection string (ADR-0012). Never the application role's.</summary>
    public const string ConnectionStringName = "TenantFlowOwner";

    public static async Task<int> RunAsync(
        string[] args,
        Action<ILoggingBuilder>? configureLogging = null,
        CancellationToken cancellationToken = default)
    {
        var builder = Host.CreateApplicationBuilder(args);
        // EF Core logs an error while it probes for a migrations history table that does not exist
        // yet, which is normal on a first run. Real failures are logged below, as one clear line.
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None);
        configureLogging?.Invoke(builder.Logging);
        var connectionString = builder.Configuration.GetConnectionString(ConnectionStringName);
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            builder.Services.AddTenantsPersistence(connectionString);
            builder.Services.AddIdentityAccessPersistence(connectionString);
            builder.Services.AddCatalogPersistence(connectionString);
            builder.Services.AddInventoryPersistence(connectionString);
            builder.Services.AddOrdersPersistence(connectionString);
            builder.Services.AddNotificationsPersistence(connectionString);
            builder.Services.AddPaymentsPersistence(connectionString);
            builder.Services.AddSubscriptionsPersistence(connectionString);
            builder.Services.AddImportsExportsPersistence(connectionString);
            builder.Services.AddAuditPersistence(connectionString);
            builder.Services.AddOperationsPersistence(connectionString);
            builder.Services.AddSchedulerPersistence(connectionString);
        }

        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("TenantFlow.Migrator");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Names the key, never a value (ADR-0008).
            LogMissingConnectionString(logger);
            return 1;
        }

        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            // Module order is irrelevant today: no foreign key crosses module schemas (ADR-0012).
            DbContext[] modules =
            [
                scope.ServiceProvider.GetRequiredService<TenantsDbContext>(),
                scope.ServiceProvider.GetRequiredService<IdentityAccessDbContext>(),
                scope.ServiceProvider.GetRequiredService<CatalogDbContext>(),
                scope.ServiceProvider.GetRequiredService<InventoryDbContext>(),
                scope.ServiceProvider.GetRequiredService<OrdersDbContext>(),
                scope.ServiceProvider.GetRequiredService<NotificationsDbContext>(),
                scope.ServiceProvider.GetRequiredService<PaymentsDbContext>(),
                scope.ServiceProvider.GetRequiredService<SubscriptionsDbContext>(),
                scope.ServiceProvider.GetRequiredService<ImportsExportsDbContext>(),
                scope.ServiceProvider.GetRequiredService<AuditDbContext>(),
                scope.ServiceProvider.GetRequiredService<OperationsDbContext>(),
                scope.ServiceProvider.GetRequiredService<SchedulerDbContext>(),
            ];
            foreach (var module in modules)
            {
                var pending = (await module.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
                LogApplying(logger, module.GetType().Name, pending.Count);
                await module.Database.MigrateAsync(cancellationToken);
            }

            LogCompleted(logger);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Each migration runs in its own transaction, so a cancelled run leaves no partial migration.
            LogCancelled(logger);
            return 1;
        }
        catch (Exception exception)
        {
            // The exception type and message only: connection errors never include the password.
            LogFailed(logger, exception.GetType().Name, exception.Message);
            return 1;
        }
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Error,
        Message = "ConnectionStrings:" + ConnectionStringName + " is required. In Development it comes from appsettings.Development.json or user-secrets; elsewhere set it through the environment or a secret store.")]
    private static partial void LogMissingConnectionString(ILogger logger);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Information, Message = "Migrating {Module}: {PendingCount} pending migration(s)")]
    private static partial void LogApplying(ILogger logger, string module, int pendingCount);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Information, Message = "All module migrations are applied")]
    private static partial void LogCompleted(ILogger logger);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Warning, Message = "Migration cancelled; run the Migrator again to finish")]
    private static partial void LogCancelled(ILogger logger);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Error, Message = "Migration failed: {ExceptionType}: {Reason}")]
    private static partial void LogFailed(ILogger logger, string exceptionType, string reason);
}
