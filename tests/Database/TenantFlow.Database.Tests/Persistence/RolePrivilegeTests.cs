using Npgsql;

namespace TenantFlow.Database.Tests.Persistence;

/// <summary>
/// SPEC-0002 criterion 3 (ADR-0011, ADR-0012): the application role owns nothing, has no elevated
/// attributes, and cannot run DDL, while it can use the module schemas. The audit-table part of the
/// criterion is proven in S5, where the audit table is created.
/// </summary>
public sealed class RolePrivilegeTests : IAsyncLifetime
{
    private TestDatabase? _database;

    private TestDatabase Database => _database ?? throw new InvalidOperationException("Not initialized.");

    public async ValueTask InitializeAsync()
    {
        var database = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var (exitCode, logs) = await MigratorRun.RunAsync(database.OwnerConnectionString);
        if (exitCode != 0)
        {
            // Dispose here: cleanup must not depend on DisposeAsync running after a failed initialization.
            await database.DisposeAsync();
            Assert.Fail(string.Join(Environment.NewLine, logs));
        }

        _database = database;
    }

    public async ValueTask DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Fact]
    public async Task Application_role_has_no_elevated_attributes()
    {
        var elevated = await MigratorRun.QueryAsync<bool>(
            Database.OwnerConnectionString,
            "SELECT rolsuper OR rolbypassrls OR rolcreaterole OR rolcreatedb OR rolreplication FROM pg_roles WHERE rolname = @role",
            ("role", TestDatabase.AppRole));

        Assert.Equal([false], elevated);
    }

    [Fact]
    public async Task Application_role_owns_no_schema_or_relation()
    {
        var owned = await MigratorRun.QueryAsync<string>(
            Database.OwnerConnectionString,
            """
            SELECT nspname::text FROM pg_namespace WHERE nspowner = (SELECT oid FROM pg_roles WHERE rolname = @role)
            UNION ALL
            SELECT relname::text FROM pg_class WHERE relowner = (SELECT oid FROM pg_roles WHERE rolname = @role)
            """,
            ("role", TestDatabase.AppRole));

        Assert.Empty(owned);
    }

    [Theory]
    [InlineData("CREATE TABLE tenants.ddl_probe (id int)")]
    [InlineData("CREATE TABLE identity_access.ddl_probe (id int)")]
    [InlineData("CREATE TABLE public.ddl_probe (id int)")]
    [InlineData("CREATE TABLE catalog.ddl_probe (id int)")]
    [InlineData("CREATE TABLE inventory.ddl_probe (id int)")]
    [InlineData("CREATE TABLE orders.ddl_probe (id int)")]
    [InlineData("CREATE TABLE notifications.ddl_probe (id int)")]
    [InlineData("CREATE TABLE payments.ddl_probe (id int)")]
    [InlineData("CREATE TABLE subscriptions.ddl_probe (id int)")]
    [InlineData("CREATE TABLE scheduler.ddl_probe (id int)")]
    [InlineData("CREATE TABLE imports_exports.ddl_probe (id int)")]
    [InlineData("CREATE TABLE audit.ddl_probe (id int)")]
    [InlineData("CREATE TABLE operations.ddl_probe (id int)")]
    [InlineData("CREATE SCHEMA ddl_probe")]
    public async Task Application_role_cannot_run_ddl(string ddl)
    {
        await using var connection = new NpgsqlConnection(Database.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => TestDatabase.ExecuteAsync(connection, ddl, TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    [Theory]
    [InlineData("tenants")]
    [InlineData("identity_access")]
    [InlineData("catalog")]
    [InlineData("inventory")]
    [InlineData("orders")]
    [InlineData("notifications")]
    [InlineData("payments")]
    [InlineData("subscriptions")]
    [InlineData("scheduler")]
    [InlineData("imports_exports")]
    [InlineData("audit")]
    [InlineData("operations")]
    public async Task Application_role_can_use_module_schemas_and_future_tables(string schema)
    {
        var usage = await MigratorRun.QueryAsync<bool>(
            Database.AppConnectionString,
            "SELECT has_schema_privilege(current_user, @schema, 'USAGE')",
            ("schema", schema));
        // Default privileges: tables the owner creates later in this schema are readable and writable
        // (a = INSERT, r = SELECT, w = UPDATE, d = DELETE) by the application role, and nothing more.
        var tableDefaults = await MigratorRun.QueryAsync<string>(
            Database.OwnerConnectionString,
            """
            SELECT acl::text FROM pg_default_acl d
            JOIN pg_namespace n ON n.oid = d.defaclnamespace, unnest(d.defaclacl) AS acl
            WHERE n.nspname = @schema AND d.defaclobjtype = 'r'
            """,
            ("schema", schema));

        Assert.Equal([true], usage);
        Assert.Equal([$"{TestDatabase.AppRole}=arwd/{TestDatabase.OwnerRole}"], tableDefaults);
    }
}
