using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TenantFlow.Migrator.Provisioning;

/// <summary>
/// <c>TenantFlow.Migrator provision</c>: what must exist before the Migrator can run, created by the server's
/// administrator. The database roles always (and Keycloak's database when its password is given); the broker's
/// application user when the management API is given. Locally Compose runs it before the Migrator on every start; in AWS
/// the operator runs it as a one-off ECS task on the first deploy and after a password rotation (deploy/terraform).
/// Returns the process exit code: 0 on success, 1 on failure.
/// </summary>
public static partial class ProvisioningApplication
{
    public const string Command = "provision";

    public static async Task<int> RunAsync(
        string[] args,
        Action<ILoggingBuilder>? configureLogging = null,
        CancellationToken cancellationToken = default)
    {
        var builder = Host.CreateApplicationBuilder(args);
        configureLogging?.Invoke(builder.Logging);
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("TenantFlow.Provisioning");
        var options = Bind(builder.Configuration.GetSection(ProvisioningOptions.SectionName));

        var missing = Missing(options).ToList();
        if (missing.Count > 0)
        {
            // Names the keys, never a value (ADR-0008).
            LogMissingSettings(logger, string.Join(", ", missing));
            return 1;
        }

        try
        {
            await DatabaseProvisioner.ProvisionAsync(options.Database, cancellationToken);
            LogDatabaseProvisioned(logger, string.IsNullOrWhiteSpace(options.Database.KeycloakPassword) ? "the TenantFlow roles" : "the TenantFlow roles and Keycloak's database");
            if (!string.IsNullOrWhiteSpace(options.Broker.ManagementUrl))
            {
                await BrokerProvisioner.ProvisionAsync(options.Broker, cancellationToken);
                LogBrokerProvisioned(logger, options.Broker.VirtualHost!);
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogCancelled(logger);
            return 1;
        }
        catch (Exception exception)
        {
            // The exception type and message only: neither carries a password (statements and request bodies are not logged).
            LogFailed(logger, exception.GetType().Name, exception.Message);
            return 1;
        }
    }

    private static ProvisioningOptions Bind(IConfigurationSection section)
    {
        var options = new ProvisioningOptions();
        section.GetSection("Database").Bind(options.Database);
        section.GetSection("Broker").Bind(options.Broker);
        return options;
    }

    private static IEnumerable<string> Missing(ProvisioningOptions options)
    {
        var database = options.Database;
        foreach (var (key, value) in new[]
        {
            ("Host", database.Host), ("Database", database.Database), ("Username", database.Username), ("Password", database.Password),
            ("OwnerPassword", database.OwnerPassword), ("AppPassword", database.AppPassword),
        })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                yield return $"{ProvisioningOptions.SectionName}:Database:{key}";
            }
        }

        if (!Enum.TryParse<Npgsql.SslMode>(database.SslMode, ignoreCase: true, out _))
        {
            yield return $"{ProvisioningOptions.SectionName}:Database:SslMode";
        }

        var broker = options.Broker;
        if (string.IsNullOrWhiteSpace(broker.ManagementUrl))
        {
            yield break;
        }

        foreach (var (key, value) in new[]
        {
            ("OperatorUsername", broker.OperatorUsername), ("OperatorPassword", broker.OperatorPassword),
            ("AppUsername", broker.AppUsername), ("AppPassword", broker.AppPassword), ("VirtualHost", broker.VirtualHost),
        })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                yield return $"{ProvisioningOptions.SectionName}:Broker:{key}";
            }
        }
    }

    [LoggerMessage(EventId = 3101, Level = LogLevel.Error, Message = "Provisioning settings are missing or invalid: {Keys}")]
    private static partial void LogMissingSettings(ILogger logger, string keys);

    [LoggerMessage(EventId = 3102, Level = LogLevel.Information, Message = "Provisioned {What}")]
    private static partial void LogDatabaseProvisioned(ILogger logger, string what);

    [LoggerMessage(EventId = 3103, Level = LogLevel.Information, Message = "Provisioned the broker's application user in virtual host {VirtualHost}; the management API refuses it")]
    private static partial void LogBrokerProvisioned(ILogger logger, string virtualHost);

    [LoggerMessage(EventId = 3104, Level = LogLevel.Warning, Message = "Provisioning cancelled; run it again, it is idempotent")]
    private static partial void LogCancelled(ILogger logger);

    [LoggerMessage(EventId = 3105, Level = LogLevel.Error, Message = "Provisioning failed: {ExceptionType}: {Reason}")]
    private static partial void LogFailed(ILogger logger, string exceptionType, string reason);
}
