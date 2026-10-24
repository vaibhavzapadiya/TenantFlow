using Npgsql;

namespace TenantFlow.Database.Tests.Support;

/// <summary>
/// A uniquely named, empty database for one test, created on the Compose PostgreSQL and dropped on
/// dispose. Database-level grants mirror what the Migrator's <c>provision</c> command (DatabaseProvisioner) grants on the main
/// database; the roles themselves are cluster-wide and provisioned by that script.
/// </summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    public const string OwnerRole = "tenantflow_owner";
    public const string AppRole = "tenantflow_app";

    /// <summary>Every test database name starts with this, followed by the run id.</summary>
    public const string NamePrefix = "tenantflow_test_";

    private readonly string _adminConnectionString;

    private TestDatabase(string name, string adminConnectionString, string ownerConnectionString, string appConnectionString)
    {
        Name = name;
        _adminConnectionString = adminConnectionString;
        OwnerConnectionString = ownerConnectionString;
        AppConnectionString = appConnectionString;
    }

    public string Name { get; }

    public string OwnerConnectionString { get; }

    public string AppConnectionString { get; }

    public static Task<TestDatabase> CreateAsync(CancellationToken cancellationToken) =>
        CreateAsync(DatabaseTestSettings.Current, cancellationToken);

    public static async Task<TestDatabase> CreateAsync(DatabaseTestSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // At most 16 + 14 + 1 + 32 = 63 characters, PostgreSQL's identifier limit.
        var name = $"{NamePrefix}{settings.RunId}_{Guid.NewGuid():N}";

        await using var admin = new NpgsqlConnection(settings.AdminConnectionString);
        try
        {
            await admin.OpenAsync(cancellationToken);
        }
        catch (NpgsqlException exception)
        {
            throw new InvalidOperationException($"{DatabaseTestSettings.HowToRun} PostgreSQL is unreachable.", exception);
        }

        await ExecuteAsync(admin, $"""CREATE DATABASE "{name}" """, cancellationToken);
        var database = new TestDatabase(
            name,
            settings.AdminConnectionString,
            WithDatabase(settings.OwnerConnectionString, name),
            WithDatabase(settings.AppConnectionString, name));
        try
        {
            await ExecuteAsync(admin, $"""GRANT CONNECT, CREATE ON DATABASE "{name}" TO {OwnerRole}""", cancellationToken);
            await ExecuteAsync(admin, $"""GRANT CONNECT ON DATABASE "{name}" TO {AppRole}""", cancellationToken);
            return database;
        }
        catch
        {
            // For example the roles are not provisioned yet: never leave the database behind.
            await database.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Pooled connections would otherwise keep the database open; FORCE ends any that remain.
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(_adminConnectionString);
        await admin.OpenAsync();
        await ExecuteAsync(admin, $"""DROP DATABASE IF EXISTS "{Name}" WITH (FORCE)""", CancellationToken.None);
    }

    public static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string WithDatabase(string connectionString, string database) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString;
}
