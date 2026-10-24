using Npgsql;

namespace TenantFlow.Migrator.Provisioning;

/// <summary>
/// Creates or re-asserts TenantFlow's database roles (ADR-0011, ADR-0012) and, when asked, Keycloak's role and database
/// (ADR-0035), as the server's administrator. Idempotent: run it on every start locally, and in AWS on the first deploy
/// and after every password rotation. A password is never part of a command the client builds: the server quotes it
/// (<c>format('%L')</c>) into the statement it then runs.
/// </summary>
internal static class DatabaseProvisioner
{
    public const string OwnerRole = "tenantflow_owner";
    public const string AppRole = "tenantflow_app";
    public const string KeycloakRole = "keycloak";
    public const string KeycloakDatabase = "keycloak";

    // Every role logs in and has no other attribute: no superuser, no role or database creation, no replication, and
    // never BYPASSRLS, so row-level security holds for the application (ADR-0011).
    private const string RoleAttributes = "LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS";

    public static async Task ProvisionAsync(DatabaseProvisioningOptions options, CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = options.Host,
            Port = options.Port,
            Database = options.Database,
            Username = options.Username,
            Password = options.Password,
            SslMode = Enum.Parse<SslMode>(options.SslMode, ignoreCase: true),
            // Provisioning runs a few statements once; a pooled connection would outlive the process for nothing.
            Pooling = false,
        };
        if (!string.IsNullOrWhiteSpace(options.RootCertificate))
        {
            builder.RootCertificate = options.RootCertificate;
        }

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        // Owner: used only by the Migrator; owns the module schemas and tables it creates. Schema creation is its only
        // database-level right. Application: used by the API and Worker; owns nothing and cannot run DDL. Module
        // migrations grant it USAGE on each schema and default privileges on its future tables.
        await EnsureRoleAsync(connection, OwnerRole, options.OwnerPassword!, cancellationToken);
        await EnsureRoleAsync(connection, AppRole, options.AppPassword!, cancellationToken);
        await RunFormattedAsync(connection, $"SELECT format('GRANT CONNECT, CREATE ON DATABASE %I TO {OwnerRole}', current_database())", cancellationToken);
        await RunFormattedAsync(connection, $"SELECT format('GRANT CONNECT ON DATABASE %I TO {AppRole}', current_database())", cancellationToken);

        if (!string.IsNullOrWhiteSpace(options.KeycloakPassword))
        {
            await ProvisionKeycloakAsync(connection, options.KeycloakPassword, cancellationToken);
        }
    }

    // Keycloak's own database on the same server. Neither side reaches the other's database: only roles granted CONNECT
    // explicitly can connect to either.
    private static async Task ProvisionKeycloakAsync(NpgsqlConnection connection, string password, CancellationToken cancellationToken)
    {
        await EnsureRoleAsync(connection, KeycloakRole, password, cancellationToken);
        // PostgreSQL 16 and later: creating a database owned by another role needs membership in it (granted once).
        if (!await ScalarAsync<bool>(connection, $"SELECT pg_has_role(current_user, '{KeycloakRole}', 'MEMBER')", cancellationToken))
        {
            await ExecuteAsync(connection, $"GRANT {KeycloakRole} TO CURRENT_USER", cancellationToken);
        }

        if (!await ScalarAsync<bool>(connection, $"SELECT EXISTS (SELECT FROM pg_database WHERE datname = '{KeycloakDatabase}')", cancellationToken))
        {
            await ExecuteAsync(connection, $"CREATE DATABASE {KeycloakDatabase} OWNER {KeycloakRole}", cancellationToken);
        }

        await ExecuteAsync(connection, $"REVOKE ALL ON DATABASE {KeycloakDatabase} FROM PUBLIC", cancellationToken);
        await RunFormattedAsync(connection, "SELECT format('REVOKE CONNECT ON DATABASE %I FROM PUBLIC', current_database())", cancellationToken);
    }

    // Roles are cluster-wide: created once, then their attributes and password re-asserted on every run.
    private static async Task EnsureRoleAsync(NpgsqlConnection connection, string role, string password, CancellationToken cancellationToken)
    {
        await using (var exists = new NpgsqlCommand("SELECT EXISTS (SELECT FROM pg_roles WHERE rolname = @role)", connection))
        {
            exists.Parameters.AddWithValue("role", role);
            if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken))!)
            {
                await ExecuteAsync(connection, $"CREATE ROLE {role}", cancellationToken);
            }
        }

        await using var alter = new NpgsqlCommand($"SELECT format('ALTER ROLE %I WITH {RoleAttributes} PASSWORD %L', @role, @password)", connection);
        alter.Parameters.AddWithValue("role", role);
        alter.Parameters.AddWithValue("password", password);
        var statement = (string)(await alter.ExecuteScalarAsync(cancellationToken))!;
        await ExecuteAsync(connection, statement, cancellationToken);
    }

    // Runs the statement the server builds: utility statements take no parameters, so identifiers are quoted by format().
    private static async Task RunFormattedAsync(NpgsqlConnection connection, string formatQuery, CancellationToken cancellationToken) =>
        await ExecuteAsync(connection, await ScalarAsync<string>(connection, formatQuery, cancellationToken), cancellationToken);

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
