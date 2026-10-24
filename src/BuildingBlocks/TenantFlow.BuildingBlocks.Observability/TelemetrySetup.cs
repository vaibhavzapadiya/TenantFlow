using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace TenantFlow.BuildingBlocks.Observability;

/// <summary>
/// Telemetry conventions for every TenantFlow host (ADR-0007, SPEC-0001): OpenTelemetry traces,
/// metrics, and logs exported over OTLP, plus JSON console logs for local reading.
/// </summary>
public static class TelemetrySetup
{
    public const string ServiceNamespace = "tenantflow";

    private const string UrlQueryAttribute = "url.query";

    /// <summary>The activity source and meter name Npgsql publishes under.</summary>
    private const string NpgsqlSource = "Npgsql";

    /// <summary>
    /// Configures telemetry for a host. The OTLP endpoint comes from the standard
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> setting (default <c>http://localhost:4317</c>).
    /// Export runs in the background: a missing collector costs data, never requests.
    /// </summary>
    /// <param name="builder">The host builder.</param>
    /// <param name="serviceName">The <c>service.name</c> resource attribute, for example <c>tenantflow-api</c>.</param>
    /// <param name="sources">Activity source and meter names the host owns, for example <c>TenantFlow.Worker</c>.</param>
    public static WebApplicationBuilder AddTenantFlowTelemetry(
        this WebApplicationBuilder builder, string serviceName, params string[] sources)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var serviceVersion = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

        // ADR-0007 amendment 1: one readable line per record on a developer's console; JSON everywhere else,
        // where log tooling reads the console. Trace context still reaches Loki through OTLP either way.
        if (builder.Environment.IsDevelopment())
        {
            builder.Logging.AddSimpleConsole(static options =>
            {
                options.SingleLine = true;
                options.UseUtcTimestamp = true;
                options.TimestampFormat = "HH:mm:ss.fff ";
            });
        }
        else
        {
            builder.Logging.AddJsonConsole(static options =>
            {
                options.IncludeScopes = true;
                options.UseUtcTimestamp = true;
                options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            });
        }

        // ADR-0031: exported log records carry allow-listed scope keys and redacted third-party exception messages.
        var scopes = new ExternalScopeCapture();
        builder.Logging.AddProvider(scopes);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName, serviceNamespace: ServiceNamespace, serviceVersion: serviceVersion)
                .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(tracing => tracing
                .AddSource(sources)
                // Npgsql's own spans, one per database command (SPEC-0010 criterion 2): statement text, never parameter values.
                .AddSource(NpgsqlSource)
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.Filter = static context => !IsHealthProbe(context);
                    // SPEC-0010 criterion 6 (DP-A25): a query string can carry personal data, keys included; the
                    // instrumentation's own redaction keeps the keys, so the attribute is dropped. url.path stays.
                    options.EnrichWithHttpRequest = static (activity, _) => activity.SetTag(UrlQueryAttribute, null);
                })
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddMeter(sources)
                // Npgsql's command durations and connection pool usage (SPEC-0010 criterion 1).
                .AddMeter(NpgsqlSource)
                .AddMeter(HealthStatusPublisher.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithLogging(logging => logging.AddProcessor(new LogRedactionProcessor(scopes)))
            .UseOtlpExporter();

        // Health checks as a gauge for the dependency alerts (SPEC-0010 criterion 11).
        builder.Services.AddSingleton<IHealthCheckPublisher, HealthStatusPublisher>();
        builder.Services.Configure<HealthCheckPublisherOptions>(static options =>
        {
            options.Delay = TimeSpan.FromSeconds(5);
            options.Period = HealthStatusPublisher.Period;
        });

        builder.Services.Configure<OpenTelemetryLoggerOptions>(static options =>
        {
            // Scopes reach the export only through LogRedactionProcessor's allow-list.
            options.IncludeScopes = false;
            options.IncludeFormattedMessage = true;
        });

        return builder;
    }

    // Probes run every few seconds; tracing them would bury real requests. Their outcome is
    // still visible through the request-duration metric and the platform's probe history.
    private static bool IsHealthProbe(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);
}
