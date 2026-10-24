using Npgsql;
using TenantFlow.BuildingBlocks.Abstractions.Errors;

namespace TenantFlow.Database.Tests.IdentityAccess;

/// <summary>
/// SPEC-0002 criterion 21: a membership change and its audit row are one transaction. The test makes the database
/// refuse the audit row, then checks that the membership change did not survive. It has its own database because it
/// changes the audit table.
/// </summary>
public sealed class MembershipAtomicityTests : IAsyncLifetime
{
    private TestDatabase? _database;
    private TenantHarness? _harness;

    private TestDatabase Database => _database ?? throw new InvalidOperationException("Not initialized.");

    private TenantHarness Harness => _harness ?? throw new InvalidOperationException("Not initialized.");

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

    [Fact]
    public async Task A_membership_change_does_not_survive_a_failed_audit_write()
    {
        var tenant = await Harness.CreateTenantAsync();
        var boss = await Harness.AddAsync(tenant, "boss", "tenant-admin");
        var member = await Harness.AddAsync(tenant, "user-1", "customer");
        await ExecuteAsOwnerAsync(
            "ALTER TABLE identity_access.membership_audit ADD CONSTRAINT ck_test_refuse CHECK (action NOT IN ('membership.added', 'membership.role_changed', 'membership.removed')) NOT VALID");

        await Assert.ThrowsAnyAsync<Exception>(() => Harness.AddAsync(tenant, "user-2", "customer"));
        await Assert.ThrowsAnyAsync<Exception>(() => Harness.ChangeRoleAsync(tenant, member.Id, "tenant-admin"));
        await Assert.ThrowsAnyAsync<Exception>(() => Harness.RemoveAsync(tenant, member.Id));

        Assert.Equal(["tenant-admin|active", "customer|active"], await MigratorRun.QueryAsync<string>(
            Database.OwnerConnectionString,
            "SELECT role || '|' || status FROM identity_access.memberships WHERE tenant_id = @t ORDER BY subject",
            ("t", tenant)));
        Assert.Equal(boss.TenantId, tenant);
    }

    [Fact]
    public async Task A_failed_change_releases_the_tenant_lock_so_the_next_change_succeeds()
    {
        var tenant = await Harness.CreateTenantAsync();
        await Harness.AddAsync(tenant, "boss", "tenant-admin");
        var duplicate = await Assert.ThrowsAsync<AppException>(() => Harness.AddAsync(tenant, "boss", "customer"));

        var next = await Harness.AddAsync(tenant, "user-1", "customer");

        Assert.Equal("membership.already_exists", duplicate.Error.Code);
        Assert.Equal("customer", next.Role);
    }

    private async Task ExecuteAsOwnerAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Database.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await TestDatabase.ExecuteAsync(connection, sql, TestContext.Current.CancellationToken);
    }
}
