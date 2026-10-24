using System.Security.Cryptography;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(TenantFlow.Database.Tests.Support.PostgreSqlContainerFixture))]

namespace TenantFlow.Database.Tests.Support;

/// <summary>
/// Open Decision 21 (ADR-0019): where no Compose database is configured but <c>TENANTFLOW_TEST_CONTAINER=1</c> is set
/// (CI on Linux), the database tests start their own PostgreSQL with Testcontainers, the same image as Compose, and
/// provision the same two roles. Locally the tests use the running Compose stack (<see cref="LocalStack"/>); without a
/// reachable server they fail with the instructions (SPEC-0002 criterion 4). After a local run this fixture checks that
/// no test database of the run is left.
/// </summary>
public sealed class PostgreSqlContainerFixture : IAsyncLifetime
{
    public const string OptInVariable = "TENANTFLOW_TEST_CONTAINER";

    // Same image and digest as deploy/compose/compose.yaml, so CI tests the PostgreSQL that developers run
    // (ContainerImageTests checks it).
    private const string Image = "postgres:17.11@sha256:b8faeff7712663c2773a190181e1246e37e364b887611c04658c28b44e2408fe";

    private PostgreSqlContainer? _container;

    public async ValueTask InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable(DatabaseTestSettings.AdminVariable) is { Length: > 0 }
            || Environment.GetEnvironmentVariable(OptInVariable) != "1")
        {
            return;
        }

        // Passwords exist only for this container's lifetime and are never written anywhere (AGENTS.md rule 6).
        var ownerPassword = NewPassword();
        var appPassword = NewPassword();
        _container = new PostgreSqlBuilder(Image).Build();
        await _container.StartAsync(TestContext.Current.CancellationToken);

        var admin = _container.GetConnectionString();
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            // The same attributes as the Migrator's provision command (DatabaseProvisioner): no superuser, no BYPASSRLS.
            await TestDatabase.ExecuteAsync(connection, $"CREATE ROLE {TestDatabase.OwnerRole} WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{ownerPassword}'", TestContext.Current.CancellationToken);
            await TestDatabase.ExecuteAsync(connection, $"CREATE ROLE {TestDatabase.AppRole} WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{appPassword}'", TestContext.Current.CancellationToken);
        }

        DatabaseTestSettings.UseContainer(new DatabaseTestSettings(
            admin,
            WithUser(admin, TestDatabase.OwnerRole, ownerPassword),
            WithUser(admin, TestDatabase.AppRole, appPassword),
            Guid.NewGuid().ToString("N")[..8]));
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }

        await AssertNoTestDatabaseIsLeftAsync();
    }

    // SPEC-0002 criterion 4: every test database of this run is dropped. A failure here fails the run.
    private static async Task AssertNoTestDatabaseIsLeftAsync()
    {
        var settings = DatabaseTestSettings.Current;
        await using var connection = new NpgsqlConnection(settings.AdminConnectionString);
        try
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
        }
        catch (NpgsqlException)
        {
            // No server: the tests have already failed with the instructions.
            return;
        }

        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE starts_with(datname, @prefix)", connection);
        command.Parameters.AddWithValue("prefix", $"{TestDatabase.NamePrefix}{settings.RunId}_");
        var left = Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        if (left != 0)
        {
            throw new InvalidOperationException($"Database tests left {left} test database(s) behind (run {settings.RunId}).");
        }
    }

    // Hex only: safe inside a SQL string literal and a connection string.
    private static string NewPassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    private static string WithUser(string connectionString, string user, string password) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Username = user, Password = password, Database = null }.ConnectionString;
}
