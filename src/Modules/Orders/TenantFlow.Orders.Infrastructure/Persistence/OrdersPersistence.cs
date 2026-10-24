using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace TenantFlow.Orders.Infrastructure.Persistence;

/// <summary>Registers the Orders module's database context (ADR-0012).</summary>
public static class OrdersPersistence
{
    /// <summary>The DbContext only: what the Migrator needs. Hosts that serve requests use <c>AddOrdersModule</c>.</summary>
    public static IServiceCollection AddOrdersPersistence(this IServiceCollection services, string connectionString) =>
        services.AddDbContext<OrdersDbContext>(options => Configure(options, connectionString));

    internal static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        // The migrations history lives in the module's own schema, so each module migrates independently.
        options.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsHistoryTable("__ef_migrations_history", OrdersDbContext.Schema));
}
