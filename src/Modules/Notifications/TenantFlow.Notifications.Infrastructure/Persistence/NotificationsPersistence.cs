using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace TenantFlow.Notifications.Infrastructure.Persistence;

/// <summary>Registers the Notifications module's database context (ADR-0012).</summary>
public static class NotificationsPersistence
{
    /// <summary>The DbContext only: what the Migrator needs. Hosts that consume messages use <c>AddNotificationsModule</c>.</summary>
    public static IServiceCollection AddNotificationsPersistence(this IServiceCollection services, string connectionString) =>
        services.AddDbContext<NotificationsDbContext>(options => Configure(options, connectionString));

    internal static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        // The migrations history lives in the module's own schema, so each module migrates independently.
        options.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsHistoryTable("__ef_migrations_history", NotificationsDbContext.Schema));
}
