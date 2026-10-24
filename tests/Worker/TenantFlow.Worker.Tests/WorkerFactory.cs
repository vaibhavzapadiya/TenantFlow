using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TenantFlow.Testing;

namespace TenantFlow.Worker.Tests;

/// <summary>
/// Creates in-memory worker hosts for tests, in the Production environment. The database and broker settings point at a
/// closed port with placeholder credentials, so the host starts but never reaches a real dependency.
/// </summary>
internal static class WorkerFactory
{
    public static readonly IReadOnlyDictionary<string, string> UnreachableDependencies = new Dictionary<string, string>
    {
        ["ConnectionStrings:TenantFlow"] = "Host=127.0.0.1;Port=1;Database=placeholder;Username=placeholder;Password=placeholder;Timeout=1",
        ["Messaging:RabbitMq:Host"] = "127.0.0.1",
        ["Messaging:RabbitMq:Port"] = "1",
        ["Messaging:RabbitMq:Username"] = "placeholder",
        ["Messaging:RabbitMq:Password"] = "placeholder",
        // Test hosts take payments with the simulator only (ADR-0022): it must be switched on outside Development.
        ["Payments:Simulator:Enabled"] = "true",
        // Filesystem storage must be switched on outside Development (ADR-0024 amendment 1).
        ["Storage:Provider"] = "filesystem",
        ["Storage:Filesystem:Enabled"] = "true",
        ["Storage:Root"] = Path.Combine(Path.GetTempPath(), "tenantflow-tests", $"storage-{Environment.ProcessId}"),
    };

    public static WebApplicationFactory<Program> CreateProduction(
        IReadOnlyDictionary<string, string>? settings = null,
        Action<IServiceCollection>? configureServices = null,
        TelemetryCapture? telemetry = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            foreach (var (key, value) in TelemetryCapture.TestHostSettings)
            {
                builder.UseSetting(key, value);
            }

            foreach (var (key, value) in UnreachableDependencies)
            {
                builder.UseSetting(key, value);
            }

            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            {
                builder.UseSetting(key, value);
            }

            if (configureServices is not null || telemetry is not null)
            {
                builder.ConfigureServices(services =>
                {
                    telemetry?.AttachTo(services);
                    configureServices?.Invoke(services);
                });
            }
        });
}
