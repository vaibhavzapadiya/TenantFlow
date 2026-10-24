using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace TenantFlow.Operations.Infrastructure.Persistence;

/// <summary>Registers the Operations module's database context (ADR-0012).</summary>
public static class OperationsPersistence
{
    /// <summary>The DbContext only: what the Migrator needs. Hosts use <c>AddOperationsModule</c>.</summary>
    public static IServiceCollection AddOperationsPersistence(this IServiceCollection services, string connectionString) =>
        services.AddDbContext<OperationsDbContext>(options => Configure(options, connectionString));

    internal static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        // The migrations history lives in the module's own schema, so each module migrates independently.
        options.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsHistoryTable("__ef_migrations_history", OperationsDbContext.Schema));
}
