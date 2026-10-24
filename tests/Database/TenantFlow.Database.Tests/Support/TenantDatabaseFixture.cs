namespace TenantFlow.Database.Tests.Support;

/// <summary>
/// One migrated test database shared by the tests of a class (each test uses its own tenants, so they cannot see
/// each other's rows). Dropped when the class is done.
/// </summary>
public sealed class TenantDatabaseFixture : IAsyncLifetime
{
    private TestDatabase? _database;
    private TenantHarness? _harness;

    internal TestDatabase Database => _database ?? throw new InvalidOperationException("Not initialized.");

    internal TenantHarness Harness => _harness ?? throw new InvalidOperationException("Not initialized.");

    public async ValueTask InitializeAsync()
    {
        var database = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var (exitCode, logs) = await MigratorRun.RunAsync(database.OwnerConnectionString);
        if (exitCode != 0)
        {
            await database.DisposeAsync();
            Assert.Fail(string.Join(Environment.NewLine, logs));
        }

        _database = database;
        _harness = TenantHarness.Create(database);
    }

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    internal async Task<long> CountAsync(string sql, params (string Name, object Value)[] parameters) =>
        Assert.Single(await MigratorRun.QueryAsync<long>(Database.OwnerConnectionString, sql, parameters));
}
