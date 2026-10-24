using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace TenantFlow.Audit.Infrastructure.Persistence;

/// <summary>Registers the Audit module's database context (ADR-0012).</summary>
public static class AuditPersistence
{
    /// <summary>The DbContext only: what the Migrator needs. Hosts that consume audit events use <c>AddAuditModule</c>.</summary>
    public static IServiceCollection AddAuditPersistence(this IServiceCollection services, string connectionString) =>
        services.AddDbContext<AuditDbContext>(options => Configure(options, connectionString));

    internal static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        // The migrations history lives in the module's own schema, so each module migrates independently.
        options.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsHistoryTable("__ef_migrations_history", AuditDbContext.Schema));
}
