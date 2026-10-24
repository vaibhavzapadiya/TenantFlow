using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace TenantFlow.Inventory.Infrastructure.Persistence;

/// <summary>Registers the Inventory module's database context (ADR-0012).</summary>
public static class InventoryPersistence
{
    /// <summary>The DbContext only: what the Migrator needs. Hosts that serve requests use <c>AddInventoryModule</c>.</summary>
    public static IServiceCollection AddInventoryPersistence(this IServiceCollection services, string connectionString) =>
        services.AddDbContext<InventoryDbContext>(options => Configure(options, connectionString));

    internal static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        // The migrations history lives in the module's own schema, so each module migrates independently.
        options.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsHistoryTable("__ef_migrations_history", InventoryDbContext.Schema));
}
