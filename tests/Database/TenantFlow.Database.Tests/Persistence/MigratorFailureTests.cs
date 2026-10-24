namespace TenantFlow.Database.Tests.Persistence;

/// <summary>
/// The Migrator fails with exit code 1, naming the problem without echoing configuration values
/// (ADR-0008). These tests need no database.
/// </summary>
public sealed class MigratorFailureTests
{
    private const string SentinelValue = "sentinel-value-that-must-never-appear-in-logs";

    [Fact]
    public async Task Missing_owner_connection_string_exits_one_and_names_only_the_key()
    {
        var (exitCode, logs) = await MigratorRun.RunAsync(
            ownerConnectionString: null,
            settings: new Dictionary<string, string> { ["Sentinel:Secret"] = SentinelValue });

        Assert.Equal(1, exitCode);
        var text = string.Join(Environment.NewLine, logs);
        Assert.Contains("ConnectionStrings:TenantFlowOwner", text, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelValue, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreachable_database_exits_one_without_logging_the_password()
    {
        // A closed local port: the connection is refused immediately. The password is a sentinel, not a credential.
        var (exitCode, logs) = await MigratorRun.RunAsync(
            $"Host=127.0.0.1;Port=1;Database=tenantflow;Username=tenantflow_owner;Password={SentinelValue};Timeout=2");

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain(SentinelValue, string.Join(Environment.NewLine, logs), StringComparison.Ordinal);
    }
}
