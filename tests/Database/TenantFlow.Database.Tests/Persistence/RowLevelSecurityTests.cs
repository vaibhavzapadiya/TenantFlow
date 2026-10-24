using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TenantFlow.BuildingBlocks.Persistence;
using TenantFlow.Catalog.Infrastructure.Persistence;

namespace TenantFlow.Database.Tests.Persistence;

/// <summary>
/// SPEC-0003 criteria 1-4 (ADR-0011): row-level security on the first tenant-owned table, tested through raw SQL as the
/// application role, so the policy is proven without any application filter in the way.
/// </summary>
public sealed class RowLevelSecurityTests : IAsyncLifetime
{
    private const string InsertProduct = """
        INSERT INTO catalog.products (id, tenant_id, sku, name, price_amount, price_currency, status, created_at, updated_at)
        VALUES (@id, @tenant, @sku, 'Widget', 1.5, 'EUR', 'active', now(), now())
        """;

    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private TestDatabase? _database;
    private ServiceProvider? _services;

    private TestDatabase Database => _database ?? throw new InvalidOperationException("Not initialized.");

    public async ValueTask InitializeAsync()
    {
        var database = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        try
        {
            var (exitCode, logs) = await MigratorRun.RunAsync(database.OwnerConnectionString);
            Assert.True(exitCode == 0, string.Join(Environment.NewLine, logs));
            _services = new ServiceCollection().AddCatalogPersistence(database.AppConnectionString).BuildServiceProvider();
            _database = database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_tenant_scope_reads_and_writes_only_its_own_rows()
    {
        var productA = await SeedAsync(TenantA, "A-1");
        var productB = await SeedAsync(TenantB, "B-1");

        await using var scope = _services!.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await using var transaction = await TenantScopedTransaction.BeginAsync(context.Database, TenantA, TestContext.Current.CancellationToken);

        var visible = await context.Database.SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM catalog.products").ToListAsync(TestContext.Current.CancellationToken);
        var updated = await context.Database.ExecuteSqlAsync($"UPDATE catalog.products SET name = 'Hijacked' WHERE id = {productB}", TestContext.Current.CancellationToken);
        var deleted = await context.Database.ExecuteSqlAsync($"DELETE FROM catalog.products WHERE id = {productB}", TestContext.Current.CancellationToken);

        Assert.Equal([productA], visible);
        Assert.Equal(0, updated);
        Assert.Equal(0, deleted);

        // Writing a row for another tenant is rejected by the policy's WITH CHECK, not silently dropped.
        var rejected = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlAsync(
            $"INSERT INTO catalog.products (id, tenant_id, sku, name, price_amount, price_currency, status, created_at, updated_at) VALUES ({Guid.NewGuid()}, {TenantB}, 'B-2', 'Widget', 1, 'EUR', 'active', now(), now())",
            TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, rejected.SqlState);
    }

    [Fact]
    public async Task Without_a_tenant_no_row_is_visible_and_no_row_can_be_written()
    {
        await SeedAsync(TenantA, "A-1");

        await using var connection = new NpgsqlConnection(Database.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0L, await CountAsync(connection));
        var rejected = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(connection, TenantA, "A-2"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, rejected.SqlState);
    }

    [Fact]
    public async Task A_pooled_connection_does_not_keep_the_previous_tenant()
    {
        await SeedAsync(TenantA, "A-1");

        // One physical connection, reused: the second open gets the connection the first transaction used.
        var pooled = new NpgsqlConnectionStringBuilder(Database.AppConnectionString) { MaxPoolSize = 1, Pooling = true, ApplicationName = "rls-pool-leak" }.ConnectionString;
        await using (var first = new NpgsqlConnection(pooled))
        {
            await first.OpenAsync(TestContext.Current.CancellationToken);
            await using var transaction = await first.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await TestDatabase.ExecuteAsync(first, $"SELECT set_config('{TenantScopedTransaction.SettingName}', '{TenantA}', true)", TestContext.Current.CancellationToken);
            Assert.Equal(1L, await CountAsync(first));
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        await using var second = new NpgsqlConnection(pooled);
        await second.OpenAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0L, await CountAsync(second));
    }

    [Fact]
    public async Task Row_level_security_is_forced_on_every_tenant_owned_table()
    {
        // Every table with a tenant_id column in a module schema; a new tenant-owned table is covered without editing this.
        // The exceptions are a module's outbox and workflow timers, ids only and read across tenants by the Worker
        // (ADR-0021 decision 7, ADR-0022 decision 7).
        var unprotected = await MigratorRun.QueryAsync<string>(
            Database.OwnerConnectionString,
            """
            SELECT (n.nspname || '.' || c.relname)::text FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped
            WHERE c.relkind = 'r' AND n.nspname IN ('catalog', 'inventory', 'orders', 'notifications', 'payments', 'subscriptions', 'imports_exports', 'audit') AND c.relname NOT IN ('outbox_messages', 'workflow_timers')
              AND NOT (c.relrowsecurity AND c.relforcerowsecurity AND EXISTS (SELECT 1 FROM pg_policy p WHERE p.polrelid = c.oid))
            """);
        var protectedCount = await MigratorRun.QueryAsync<long>(
            Database.OwnerConnectionString,
            "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind = 'r' AND c.relforcerowsecurity AND n.nspname IN ('catalog', 'inventory', 'orders', 'notifications', 'payments', 'subscriptions', 'imports_exports', 'audit')");

        Assert.Empty(unprotected);
        Assert.Equal([22L], protectedCount);
    }

    [Fact]
    public async Task Stock_adjustments_are_append_only_for_the_application_role()
    {
        var privileges = await MigratorRun.QueryAsync<bool>(
            Database.AppConnectionString,
            "SELECT has_table_privilege(current_user, 'inventory.stock_adjustments', p) FROM unnest(ARRAY['INSERT', 'SELECT', 'UPDATE', 'DELETE', 'TRUNCATE']) AS p");

        Assert.Equal([true, true, false, false, false], privileges);
    }

    [Fact]
    public async Task Reservations_are_never_deleted_by_the_application_role()
    {
        var privileges = await MigratorRun.QueryAsync<bool>(
            Database.AppConnectionString,
            "SELECT has_table_privilege(current_user, 'inventory.reservations', p) FROM unnest(ARRAY['INSERT', 'SELECT', 'UPDATE', 'DELETE', 'TRUNCATE']) AS p");

        Assert.Equal([true, true, true, false, false], privileges);
    }

    private async Task<Guid> SeedAsync(Guid tenant, string sku)
    {
        await using var scope = _services!.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await using var transaction = await TenantScopedTransaction.BeginAsync(context.Database, tenant, TestContext.Current.CancellationToken);
        var id = Guid.NewGuid();
        await context.Database.ExecuteSqlAsync(
            $"INSERT INTO catalog.products (id, tenant_id, sku, name, price_amount, price_currency, status, created_at, updated_at) VALUES ({id}, {tenant}, {sku}, 'Widget', 1.5, 'EUR', 'active', now(), now())",
            TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("SELECT count(*) FROM catalog.products", connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task InsertAsync(NpgsqlConnection connection, Guid tenant, string sku)
    {
        await using var command = new NpgsqlCommand(InsertProduct, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("sku", sku);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
