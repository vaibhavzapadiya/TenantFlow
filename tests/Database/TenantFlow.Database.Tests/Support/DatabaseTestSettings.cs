namespace TenantFlow.Database.Tests.Support;

/// <summary>
/// Connection settings for database tests: the local Compose stack's (<see cref="LocalStack"/>, ADR-0037), unless
/// environment variables name another server. Connection strings name no database; each test creates its own
/// (<see cref="TestDatabase"/>).
/// </summary>
internal sealed record DatabaseTestSettings(string AdminConnectionString, string OwnerConnectionString, string AppConnectionString, string RunId)
{
    public const string AdminVariable = "TENANTFLOW_TEST_ADMIN_CONNECTION";
    public const string OwnerVariable = "TENANTFLOW_TEST_OWNER_CONNECTION";
    public const string AppVariable = "TENANTFLOW_TEST_APP_CONNECTION";
    public const string RunIdVariable = "TENANTFLOW_TEST_RUN_ID";

    /// <summary>SPEC-0002 criterion 4: a missing database is a blocked run, never a silent skip.</summary>
    public const string HowToRun =
        "Database tests need the local PostgreSQL. Run docker compose up -d --wait, then dotnet run --project tests/Database/TenantFlow.Database.Tests -c Release. Where Docker is reachable directly (CI on Linux), set TENANTFLOW_TEST_CONTAINER=1 instead.";

    private static readonly Lazy<DatabaseTestSettings> FromProcessEnvironment =
        new(() => From(static name => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : LocalDefault(name)));

    private static DatabaseTestSettings? _container;

    /// <summary>The Testcontainers database when <see cref="PostgreSqlContainerFixture"/> started one, else the environment's.</summary>
    public static DatabaseTestSettings Current => _container ?? FromProcessEnvironment.Value;

    /// <summary>Called once, by the assembly fixture, before any test runs.</summary>
    internal static void UseContainer(DatabaseTestSettings settings) => _container = settings;

    public static DatabaseTestSettings From(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var runId = read(RunIdVariable) is { Length: > 0 } value ? value : Guid.NewGuid().ToString("N")[..8];
        // The run id becomes part of database names, so it is restricted to a safe identifier fragment.
        if (runId.Length > 14 || !runId.All(static c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9')))
        {
            throw new InvalidOperationException($"{RunIdVariable} must be 1 to 14 lowercase letters or digits.");
        }

        return new DatabaseTestSettings(
            Require(read, AdminVariable),
            Require(read, OwnerVariable),
            Require(read, AppVariable),
            runId);
    }

    private static string? LocalDefault(string name) => name switch
    {
        AdminVariable => LocalStack.AdminConnectionString,
        OwnerVariable => LocalStack.OwnerConnectionString,
        AppVariable => LocalStack.AppConnectionString,
        _ => null,
    };

    // The message names the variable, never a value, so a failure cannot print a credential.
    private static string Require(Func<string, string?> read, string name) =>
        read(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{HowToRun} Missing setting: {name}.");
}
