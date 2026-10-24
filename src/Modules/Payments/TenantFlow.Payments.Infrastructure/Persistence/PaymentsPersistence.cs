using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace TenantFlow.Payments.Infrastructure.Persistence;

/// <summary>Registers the Payments module's database context (ADR-0012).</summary>
public static class PaymentsPersistence
{
    /// <summary>The DbContext only: what the Migrator needs. Hosts that serve requests use <c>AddPaymentsModule</c>.</summary>
    public static IServiceCollection AddPaymentsPersistence(this IServiceCollection services, string connectionString) =>
        services.AddDbContext<PaymentsDbContext>(options => Configure(options, connectionString));

    internal static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        // The migrations history lives in the module's own schema, so each module migrates independently.
        options.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsHistoryTable("__ef_migrations_history", PaymentsDbContext.Schema));
}
