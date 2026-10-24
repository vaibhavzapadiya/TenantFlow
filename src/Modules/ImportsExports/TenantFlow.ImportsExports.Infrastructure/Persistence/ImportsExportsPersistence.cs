using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace TenantFlow.ImportsExports.Infrastructure.Persistence;

/// <summary>Registers the ImportsExports module's database context (ADR-0012).</summary>
public static class ImportsExportsPersistence
{
    /// <summary>The DbContext only: what the Migrator needs. Hosts that serve requests use <c>AddImportsExportsModule</c>.</summary>
    public static IServiceCollection AddImportsExportsPersistence(this IServiceCollection services, string connectionString) =>
        services.AddDbContext<ImportsExportsDbContext>(options => Configure(options, connectionString));

    internal static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        // The migrations history lives in the module's own schema, so each module migrates independently.
        options.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsHistoryTable("__ef_migrations_history", ImportsExportsDbContext.Schema));
}
