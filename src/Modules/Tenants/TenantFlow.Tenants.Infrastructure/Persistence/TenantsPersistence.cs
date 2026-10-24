using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace TenantFlow.Tenants.Infrastructure.Persistence;

/// <summary>Registers the Tenants module's database context (ADR-0012).</summary>
public static class TenantsPersistence
{
    /// <summary>The DbContext only: what the Migrator needs. Hosts that serve requests use <c>AddTenantsModule</c>.</summary>
    public static IServiceCollection AddTenantsPersistence(this IServiceCollection services, string connectionString) =>
        services.AddDbContext<TenantsDbContext>(options => Configure(options, connectionString));

    internal static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        // The migrations history lives in the module's own schema, so each module migrates independently.
        options.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsHistoryTable("__ef_migrations_history", TenantsDbContext.Schema));
}
