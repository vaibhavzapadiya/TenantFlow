using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace TenantFlow.Api.Tests.Support;

/// <summary>
/// Creates in-memory API hosts for tests. Hosts run in the Production environment, so no
/// Development-only defaults (user-secrets, extra validation) can hide a missing setting.
/// </summary>
internal static class ApiFactory
{
    /// <summary>The object storage folder of this test run (ADR-0024): one temporary folder per process.</summary>
    public static readonly string StorageRoot = Path.Combine(Path.GetTempPath(), "tenantflow-tests", $"storage-{Environment.ProcessId}");

    /// <summary>The audit operator-reference key of this test run (ADR-0026): random per process, never a fixed value.</summary>
    public static readonly string OperatorReferenceKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Points at a closed local port. PostgreSQL is then reliably unreachable, and the connection is
    /// refused immediately, without Docker. The password is a placeholder, not a credential.
    /// </summary>
    public const string UnreachableDatabase =
        "Host=127.0.0.1;Port=1;Database=tenantflow;Username=tenantflow;Password=placeholder;Timeout=2";

    public static WebApplicationFactory<Program> CreateProduction(
        Action<IServiceCollection>? configureServices = null,
        string? connectionString = UnreachableDatabase,
        IReadOnlyDictionary<string, string>? settings = null,
        TelemetryCapture? telemetry = null) =>
        Create("Production", configureServices, connectionString, settings, telemetry);

    /// <summary>
    /// A Development host, for parity tests only: a behaviour that differs between environments (for
    /// example minimal APIs throw on bad requests only in Development) can only be caught by running
    /// both. Everything else runs in Production (.agents/rules/tests.md).
    /// </summary>
    public static WebApplicationFactory<Program> CreateDevelopment(
        Action<IServiceCollection>? configureServices = null,
        string? connectionString = UnreachableDatabase,
        IReadOnlyDictionary<string, string>? settings = null) =>
        Create(
            "Development",
            configureServices,
            connectionString,
            // appsettings.Development.json trusts a local Keycloak over plain HTTP; the test issuers are not local.
            new Dictionary<string, string>(settings ?? new Dictionary<string, string>())
            {
                ["Authentication:Tenant:RequireHttpsMetadata"] = "true",
                ["Authentication:Platform:RequireHttpsMetadata"] = "true",
            },
            telemetry: null);

    private static WebApplicationFactory<Program> Create(
        string environment,
        Action<IServiceCollection>? configureServices,
        string? connectionString,
        IReadOnlyDictionary<string, string>? settings,
        TelemetryCapture? telemetry) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            // Always set the key: an empty value stands for "missing", so an ambient
            // ConnectionStrings__TenantFlow environment variable cannot satisfy a test by accident.
            builder.UseSetting("ConnectionStrings:TenantFlow", connectionString ?? string.Empty);
            foreach (var (key, value) in TelemetryCapture.TestHostSettings)
            {
                builder.UseSetting(key, value);
            }

            // Test hosts take payments with the simulator only (ADR-0022): it must be switched on outside Development.
            builder.UseSetting("Payments:Simulator:Enabled", "true");

            // Uploads and exports go to a folder of the test run (ADR-0024), never a developer's .storage. The filesystem
            // must be switched on outside Development (ADR-0024 amendment 1); a test's own settings win.
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:Filesystem:Enabled", "true");
            builder.UseSetting("Storage:Root", StorageRoot);

            // Platform actors reach the audit log as a keyed reference (ADR-0026 decision 3); the key is required.
            builder.UseSetting("Audit:OperatorReferenceKey", OperatorReferenceKey);

            // Every test host trusts the in-process test issuer (ADR-0009); a test's own settings win.
            foreach (var (key, value) in TestIssuer.Settings)
            {
                builder.UseSetting(key, value);
            }

            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureServices(services =>
            {
                TestIssuer.AttachAllTo(services);
                telemetry?.AttachTo(services);
                configureServices?.Invoke(services);
            });
        });
}
