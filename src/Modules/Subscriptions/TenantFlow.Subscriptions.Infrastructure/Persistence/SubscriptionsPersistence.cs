using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace TenantFlow.Subscriptions.Infrastructure.Persistence;

/// <summary>Registers the Subscriptions module's database context (ADR-0012).</summary>
public static class SubscriptionsPersistence
{
    /// <summary>The DbContext only: what the Migrator needs. Hosts that serve requests use <c>AddSubscriptionsModule</c>.</summary>
    public static IServiceCollection AddSubscriptionsPersistence(this IServiceCollection services, string connectionString) =>
        services.AddDbContext<SubscriptionsDbContext>(options => Configure(options, connectionString));

    internal static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        // The migrations history lives in the module's own schema, so each module migrates independently.
        options.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsHistoryTable("__ef_migrations_history", SubscriptionsDbContext.Schema));
}
