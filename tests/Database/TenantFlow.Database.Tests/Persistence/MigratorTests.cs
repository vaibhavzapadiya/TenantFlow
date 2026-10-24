namespace TenantFlow.Database.Tests.Persistence;

/// <summary>SPEC-0002 criterion 1: the Migrator brings an empty database up to date and is idempotent.</summary>
public sealed class MigratorTests
{
    private static readonly string[] ModuleSchemas = ["identity_access", "tenants"];

    private const string CountAppliedMigrations = """
        SELECT (SELECT count(*) FROM tenants.__ef_migrations_history)
             + (SELECT count(*) FROM identity_access.__ef_migrations_history)
        """;

    [Fact]
    public async Task Migrator_applies_every_module_migration_to_an_empty_database_and_exits_zero()
    {
        await using var database = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);

        var (exitCode, logs) = await MigratorRun.RunAsync(database.OwnerConnectionString);

        Assert.True(exitCode == 0, string.Join(Environment.NewLine, logs));
        var schemas = await MigratorRun.QueryAsync<string>(
            database.OwnerConnectionString,
            "SELECT nspname FROM pg_namespace WHERE nspname = ANY(@schemas) ORDER BY nspname",
            ("schemas", ModuleSchemas));
        Assert.Equal(ModuleSchemas, schemas);
        var applied = await MigratorRun.QueryAsync<long>(database.OwnerConnectionString, CountAppliedMigrations);
        Assert.True(applied[0] >= ModuleSchemas.Length, $"Expected at least one migration per module, found {applied[0]}.");
    }

    [Fact]
    public async Task Second_run_applies_nothing_and_exits_zero()
    {
        await using var database = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var (firstExitCode, _) = await MigratorRun.RunAsync(database.OwnerConnectionString);
        var appliedAfterFirstRun = await MigratorRun.QueryAsync<long>(database.OwnerConnectionString, CountAppliedMigrations);

        var (secondExitCode, logs) = await MigratorRun.RunAsync(database.OwnerConnectionString);

        Assert.Equal(0, firstExitCode);
        Assert.True(secondExitCode == 0, string.Join(Environment.NewLine, logs));
        Assert.Equal(appliedAfterFirstRun, await MigratorRun.QueryAsync<long>(database.OwnerConnectionString, CountAppliedMigrations));
    }
}
