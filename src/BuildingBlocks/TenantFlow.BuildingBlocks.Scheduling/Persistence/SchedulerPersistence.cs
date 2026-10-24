using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace TenantFlow.BuildingBlocks.Scheduling.Persistence;

/// <summary>Registers the scheduler schema's context, for the Migrator (ADR-0012).</summary>
public static class SchedulerPersistence
{
    public static IServiceCollection AddSchedulerPersistence(this IServiceCollection services, string connectionString) =>
        services.AddDbContext<SchedulerDbContext>(options => Configure(options, connectionString));

    internal static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        // The migrations history lives in the scheduler's own schema, like a module's.
        options.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsHistoryTable("__ef_migrations_history", SchedulerDbContext.Schema));
}
