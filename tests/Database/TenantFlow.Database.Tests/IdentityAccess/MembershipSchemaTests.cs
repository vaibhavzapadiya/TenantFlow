using Npgsql;

namespace TenantFlow.Database.Tests.IdentityAccess;

/// <summary>
/// SPEC-0002 criteria 2, 3 and 19 in the schema itself: the membership audit is append-only for the application role,
/// and the database refuses what the code should never attempt (two active memberships, unknown roles), so a code bug
/// cannot corrupt the boundary.
/// </summary>
public sealed class MembershipSchemaTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>
{
    private static readonly Guid SomeTenant = Guid.CreateVersion7();

    [Theory]
    [InlineData("UPDATE identity_access.membership_audit SET action = 'x'")]
    [InlineData("DELETE FROM identity_access.membership_audit")]
    [InlineData("TRUNCATE identity_access.membership_audit")]
    public async Task Application_role_cannot_change_or_remove_membership_audit(string statement)
    {
        // A row to act on, written the way the application writes it.
        var tenant = await fixture.Harness.CreateTenantAsync();
        await fixture.Harness.AddAsync(tenant, "user-1", "customer");

        var exception = await Assert.ThrowsAsync<PostgresException>(() => RunAsAppAsync(statement));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        Assert.True(await fixture.CountAsync("SELECT count(*) FROM identity_access.membership_audit WHERE tenant_id = @t", ("t", tenant)) >= 1);
    }

    [Fact]
    public async Task Application_role_can_read_and_append_membership_audit()
    {
        await RunAsAppAsync("SELECT count(*) FROM identity_access.membership_audit");

        await RunAsAppAsync(
            $"""
            INSERT INTO identity_access.membership_audit (tenant_id, membership_id, action, actor_issuer, actor_subject, new_role, occurred_at)
            VALUES ('{SomeTenant}', '{Guid.CreateVersion7()}', 'membership.added', 'i', 's', 'customer', now())
            """);
    }

    [Fact]
    public async Task Application_role_can_read_and_write_memberships_but_not_change_the_table()
    {
        await RunAsAppAsync("SELECT count(*) FROM identity_access.memberships");

        var exception = await Assert.ThrowsAsync<PostgresException>(() => RunAsAppAsync("ALTER TABLE identity_access.memberships ADD COLUMN probe int"));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    [Fact]
    public async Task The_database_refuses_a_second_active_membership_but_allows_a_removed_one_beside_it()
    {
        var tenant = Guid.CreateVersion7();

        await InsertMembershipAsync(tenant, "dup-user", "active");
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => InsertMembershipAsync(tenant, "dup-user", "active"));
        await InsertMembershipAsync(tenant, "dup-user", "removed");
        await InsertMembershipAsync(tenant, "dup-user", "removed");

        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
    }

    [Theory]
    [InlineData("owner", "active")]
    [InlineData("Customer", "active")]
    [InlineData("customer", "pending")]
    public async Task The_database_refuses_an_unknown_role_or_status(string role, string status)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertMembershipAsync(Guid.CreateVersion7(), "check-user", status, role));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task No_foreign_key_crosses_a_module_schema()
    {
        var crossing = await MigratorRun.QueryAsync<string>(
            fixture.Database.OwnerConnectionString,
            """
            SELECT conname::text FROM pg_constraint c
            JOIN pg_class r ON r.oid = c.conrelid JOIN pg_namespace rn ON rn.oid = r.relnamespace
            JOIN pg_class f ON f.oid = c.confrelid JOIN pg_namespace fn ON fn.oid = f.relnamespace
            WHERE c.contype = 'f' AND rn.nspname <> fn.nspname
            """);

        Assert.Empty(crossing);
    }

    private async Task RunAsAppAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await TestDatabase.ExecuteAsync(connection, sql, TestContext.Current.CancellationToken);
    }

    private Task InsertMembershipAsync(Guid tenant, string subject, string status, string role = "customer") =>
        RunAsAppAsync(
            $"""
            INSERT INTO identity_access.memberships (id, tenant_id, issuer, subject, role, status, created_at, updated_at)
            VALUES ('{Guid.CreateVersion7()}', '{tenant}', 'https://idp.test', '{subject}', '{role}', '{status}', now(), now())
            """);
}
