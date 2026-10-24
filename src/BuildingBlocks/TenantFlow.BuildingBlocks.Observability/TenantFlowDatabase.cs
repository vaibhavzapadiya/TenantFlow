using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace TenantFlow.BuildingBlocks.Observability;

/// <summary>
/// The database plumbing every host shares (ADR-0008, ADR-0012): the validated connection settings, the application
/// role's <see cref="NpgsqlDataSource"/>, and the readiness check.
/// </summary>
public static class TenantFlowDatabase
{
    public const string ConnectionStringName = "TenantFlow";

    /// <summary>
    /// Registers the settings (validated at startup), the data source and the <c>postgresql</c> readiness check. Returns
    /// the connection string for the modules' registrations. A missing one already fails startup through the validated
    /// settings, so the empty fallback is never used to serve a request or handle a message.
    /// </summary>
    public static string AddTenantFlowDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<DatabaseOptions>()
            .Configure<IConfiguration>(static (options, configuration) =>
                options.ConnectionString = configuration.GetConnectionString(ConnectionStringName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton(static provider =>
            NpgsqlDataSource.Create(provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString!));
        services.AddHealthChecks()
            .AddCheck<PostgreSqlReadinessCheck>("postgresql", tags: [HealthEndpoints.ReadyTag]);
        return configuration.GetConnectionString(ConnectionStringName) ?? string.Empty;
    }

    /// <summary>PostgreSQL connection settings, bound from <c>ConnectionStrings:TenantFlow</c>: the application role.</summary>
    internal sealed class DatabaseOptions
    {
        // The message names the key, never the value, so a startup failure cannot leak a credential.
        [Required(ErrorMessage = "ConnectionStrings:TenantFlow is required. In Development it comes from appsettings.Development.json or user-secrets; elsewhere set it through the environment or a secret store.")]
        public string? ConnectionString { get; set; }
    }
}
