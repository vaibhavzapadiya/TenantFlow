using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace TenantFlow.IdentityAccess.Infrastructure.Persistence;

/// <summary>Registers the IdentityAccess module's persistence with the host's container.</summary>
public static class IdentityAccessPersistence
{
    public static IServiceCollection AddIdentityAccessPersistence(this IServiceCollection services, string connectionString) =>
        services.AddDbContext<IdentityAccessDbContext>(options => Configure(options, connectionString));

    internal static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        // The migrations history lives in the module's own schema, so each module migrates independently.
        options.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsHistoryTable("__ef_migrations_history", IdentityAccessDbContext.Schema));
}
