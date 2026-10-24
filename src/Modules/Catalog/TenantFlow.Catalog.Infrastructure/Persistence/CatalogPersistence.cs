using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace TenantFlow.Catalog.Infrastructure.Persistence;

/// <summary>Registers the Catalog module's database context (ADR-0012).</summary>
public static class CatalogPersistence
{
    /// <summary>The DbContext only: what the Migrator needs.</summary>
    public static IServiceCollection AddCatalogPersistence(this IServiceCollection services, string connectionString) =>
        services.AddDbContext<CatalogDbContext>(options => Configure(options, connectionString));

    internal static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        // The migrations history lives in the module's own schema, so each module migrates independently.
        options.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsHistoryTable("__ef_migrations_history", CatalogDbContext.Schema));
}
