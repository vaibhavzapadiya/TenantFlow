using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Abstractions.Identity;

namespace TenantFlow.Database.Tests.IdentityAccess;

/// <summary>
/// SPEC-0002 S5a against real PostgreSQL: criteria 16 and 19 to 21 as application behaviour. The HTTP routes for
/// members arrive in S6; here the use cases are called the way those routes will call them.
/// </summary>
public sealed class MembershipServiceTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>
{
    private static readonly ActorIdentity Admin = new(TenantHarness.Issuer, "admin-actor");

    private TenantHarness Harness => fixture.Harness;

    [Fact]
    public async Task Adding_a_member_creates_an_active_membership_and_exactly_one_audit_row()
    {
        var tenant = await Harness.CreateTenantAsync();
        var at = Harness.Clock.GetUtcNow();

        var added = await Harness.AddAsync(tenant, "user-1", "customer", actor: Admin);

        Assert.Equal(tenant, added.TenantId);
        Assert.Equal("customer", added.Role);
        Assert.Equal(["customer|active"], await Rows(tenant, "SELECT role || '|' || status FROM identity_access.memberships WHERE tenant_id = @t", added.Id));
        Assert.Equal(
            [$"membership.added|{TenantHarness.Issuer}|admin-actor|{added.Id}||customer|{at.UtcDateTime:O}"],
            await AuditAsync(tenant));
    }

    [Fact]
    public async Task Audit_time_comes_from_the_clock_not_the_database()
    {
        var tenant = await Harness.CreateTenantAsync();
        Harness.Clock.Advance(TimeSpan.FromHours(3));
        var expected = Harness.Clock.GetUtcNow().UtcDateTime;

        await Harness.AddAsync(tenant, "user-1", "customer");

        var row = Assert.Single(await AuditAsync(tenant));
        Assert.EndsWith($"|{expected:O}", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adding_a_member_who_is_already_active_returns_409_and_writes_nothing()
    {
        var tenant = await Harness.CreateTenantAsync();
        await Harness.AddAsync(tenant, "user-1", "customer");

        var conflict = await Assert.ThrowsAsync<AppException>(() => Harness.AddAsync(tenant, "user-1", "tenant-admin"));

        Assert.Equal("membership.already_exists", conflict.Error.Code);
        Assert.Equal(ErrorKind.Conflict, conflict.Error.Kind);
        Assert.Equal(1, await CountMembershipsAsync(tenant));
        Assert.Single(await AuditAsync(tenant));
    }

    [Fact]
    public async Task The_same_subject_from_another_issuer_is_a_different_member()
    {
        var tenant = await Harness.CreateTenantAsync();

        var first = await Harness.AddAsync(tenant, "user-1", "customer");
        var second = await Harness.AddAsync(tenant, "user-1", "customer", TenantHarness.OtherIssuer);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, await CountMembershipsAsync(tenant));
    }

    [Fact]
    public async Task The_same_person_can_belong_to_two_tenants()
    {
        var a = await Harness.CreateTenantAsync("A");
        var b = await Harness.CreateTenantAsync("B");

        await Harness.AddAsync(a, "user-1", "customer");
        await Harness.AddAsync(b, "user-1", "tenant-admin");

        Assert.Equal(1, await CountMembershipsAsync(a));
        Assert.Equal(1, await CountMembershipsAsync(b));
    }

    [Theory]
    [InlineData("", "customer", "membership.subject.required")]
    [InlineData("   ", "customer", "membership.subject.required")]
    [InlineData("user\n1", "customer", "membership.subject.invalid_characters")]
    [InlineData("user-1", "owner", "membership.role.invalid")]
    [InlineData("user-1", "Customer", "membership.role.invalid")]
    [InlineData("user-1", "", "membership.role.invalid")]
    public async Task Invalid_input_is_rejected_and_writes_nothing(string subject, string role, string expectedCode)
    {
        var tenant = await Harness.CreateTenantAsync();

        var invalid = await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => Harness.AddAsync(tenant, subject, role));

        Assert.Contains(invalid.Errors, error => error.ErrorCode == expectedCode);
        Assert.Equal(0, await CountMembershipsAsync(tenant));
        Assert.Empty(await AuditAsync(tenant));
    }

    [Fact]
    public async Task Changing_a_role_updates_the_membership_and_writes_one_audit_row_with_both_roles()
    {
        var tenant = await Harness.CreateTenantAsync();
        await Harness.AddAsync(tenant, "boss", "tenant-admin");
        var member = await Harness.AddAsync(tenant, "user-1", "customer");

        var changed = await Harness.ChangeRoleAsync(tenant, member.Id, "tenant-admin", Admin);

        Assert.Equal("tenant-admin", changed.Role);
        Assert.Equal(["tenant-admin"], await Rows(tenant, "SELECT role FROM identity_access.memberships WHERE id = @m", member.Id));
        var audit = await AuditAsync(tenant);
        Assert.Equal(3, audit.Count);
        Assert.StartsWith($"membership.role_changed|{TenantHarness.Issuer}|admin-actor|{member.Id}|customer|tenant-admin|", audit[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setting_the_role_a_member_already_has_changes_and_audits_nothing()
    {
        var tenant = await Harness.CreateTenantAsync();
        var member = await Harness.AddAsync(tenant, "user-1", "customer");

        var unchanged = await Harness.ChangeRoleAsync(tenant, member.Id, "customer");

        Assert.Equal("customer", unchanged.Role);
        Assert.Single(await AuditAsync(tenant));
    }

    [Fact]
    public async Task Changing_the_role_through_another_tenants_route_is_not_found_and_changes_nothing()
    {
        var a = await Harness.CreateTenantAsync("A");
        var b = await Harness.CreateTenantAsync("B");
        await Harness.AddAsync(a, "boss-a", "tenant-admin");
        await Harness.AddAsync(b, "boss-b", "tenant-admin");
        var victim = await Harness.AddAsync(b, "user-b", "customer");
        var auditBefore = (await AuditAsync(a)).Count + (await AuditAsync(b)).Count;

        var change = await Assert.ThrowsAsync<AppException>(() => Harness.ChangeRoleAsync(a, victim.Id, "tenant-admin"));
        var remove = await Assert.ThrowsAsync<AppException>(() => Harness.RemoveAsync(a, victim.Id));

        Assert.Equal("membership.not_found", change.Error.Code);
        Assert.Equal("membership.not_found", remove.Error.Code);
        Assert.Equal(["customer|active"], await Rows(b, "SELECT role || '|' || status FROM identity_access.memberships WHERE id = @m", victim.Id));
        Assert.Equal(auditBefore, (await AuditAsync(a)).Count + (await AuditAsync(b)).Count);
    }

    [Fact]
    public async Task An_unknown_membership_or_an_empty_id_is_not_found()
    {
        var tenant = await Harness.CreateTenantAsync();

        var unknown = await Assert.ThrowsAsync<AppException>(() => Harness.ChangeRoleAsync(tenant, Guid.CreateVersion7(), "customer"));
        var emptyMembership = await Assert.ThrowsAsync<AppException>(() => Harness.RemoveAsync(tenant, Guid.Empty));
        var emptyTenant = await Assert.ThrowsAsync<AppException>(() => Harness.RemoveAsync(Guid.Empty, Guid.CreateVersion7()));

        Assert.All([unknown, emptyMembership, emptyTenant], error => Assert.Equal("membership.not_found", error.Error.Code));
    }

    [Fact]
    public async Task Removing_a_member_ends_the_membership_audits_it_once_and_allows_a_fresh_membership()
    {
        var tenant = await Harness.CreateTenantAsync();
        await Harness.AddAsync(tenant, "boss", "tenant-admin");
        var member = await Harness.AddAsync(tenant, "user-1", "customer");

        await Harness.RemoveAsync(tenant, member.Id, Admin);
        var again = await Assert.ThrowsAsync<AppException>(() => Harness.RemoveAsync(tenant, member.Id));
        var changeRemoved = await Assert.ThrowsAsync<AppException>(() => Harness.ChangeRoleAsync(tenant, member.Id, "tenant-admin"));
        var returned = await Harness.AddAsync(tenant, "user-1", "customer");

        Assert.Equal("membership.not_found", again.Error.Code);
        Assert.Equal("membership.not_found", changeRemoved.Error.Code);
        Assert.NotEqual(member.Id, returned.Id);
        Assert.Equal(["removed"], await Rows(tenant, "SELECT status FROM identity_access.memberships WHERE id = @m", member.Id));
        var removals = (await AuditAsync(tenant)).Where(row => row.StartsWith("membership.removed|", StringComparison.Ordinal)).ToList();
        var removal = Assert.Single(removals);
        Assert.Contains($"|admin-actor|{member.Id}|customer||", removal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_only_administrator_cannot_be_demoted_or_removed_and_nothing_is_written()
    {
        var tenant = await Harness.CreateTenantAsync();
        var only = await Harness.AddAsync(tenant, "boss", "tenant-admin");
        await Harness.AddAsync(tenant, "user-1", "customer");
        var auditBefore = (await AuditAsync(tenant)).Count;

        var demote = await Assert.ThrowsAsync<AppException>(() => Harness.ChangeRoleAsync(tenant, only.Id, "customer"));
        var remove = await Assert.ThrowsAsync<AppException>(() => Harness.RemoveAsync(tenant, only.Id));

        Assert.Equal("membership.last_administrator", demote.Error.Code);
        Assert.Equal("membership.last_administrator", remove.Error.Code);
        Assert.Equal(["tenant-admin|active"], await Rows(tenant, "SELECT role || '|' || status FROM identity_access.memberships WHERE id = @m", only.Id));
        Assert.Equal(auditBefore, (await AuditAsync(tenant)).Count);
    }

    [Fact]
    public async Task With_two_administrators_one_can_be_demoted_but_then_the_other_is_protected()
    {
        var tenant = await Harness.CreateTenantAsync();
        var first = await Harness.AddAsync(tenant, "boss-1", "tenant-admin");
        var second = await Harness.AddAsync(tenant, "boss-2", "tenant-admin");

        await Harness.ChangeRoleAsync(tenant, first.Id, "customer");
        var blocked = await Assert.ThrowsAsync<AppException>(() => Harness.RemoveAsync(tenant, second.Id));

        Assert.Equal("membership.last_administrator", blocked.Error.Code);
    }

    [Fact]
    public async Task Two_administrators_demoting_each_other_at_once_leave_exactly_one_winner_every_time()
    {
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            var tenant = await Harness.CreateTenantAsync($"Race {attempt}");
            var one = await Harness.AddAsync(tenant, "boss-1", "tenant-admin");
            var two = await Harness.AddAsync(tenant, "boss-2", "tenant-admin");
            var start = new TaskCompletionSource();

            var demoteTwo = Task.Run(async () =>
            {
                await start.Task;
                return await Attempt(() => Harness.ChangeRoleAsync(tenant, two.Id, "customer", new ActorIdentity(TenantHarness.Issuer, "boss-1")));
            });
            var demoteOne = Task.Run(async () =>
            {
                await start.Task;
                return await Attempt(() => Harness.ChangeRoleAsync(tenant, one.Id, "customer", new ActorIdentity(TenantHarness.Issuer, "boss-2")));
            });
            start.SetResult();
            var outcomes = await Task.WhenAll(demoteTwo, demoteOne);

            Assert.Single(outcomes, outcome => outcome is null);
            Assert.Single(outcomes, outcome => outcome == "membership.last_administrator");
            Assert.Equal(
                1,
                (await Rows(tenant, "SELECT count(*)::text FROM identity_access.memberships WHERE tenant_id = @t AND role = 'tenant-admin' AND status = 'active'", Guid.Empty))
                    .Select(int.Parse).Single());
            Assert.Equal(1, (await AuditAsync(tenant)).Count(row => row.StartsWith("membership.role_changed|", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public async Task Listing_returns_only_the_callers_own_active_memberships_in_active_tenants()
    {
        var mine = await Harness.CreateTenantAsync("Mine");
        var other = await Harness.CreateTenantAsync("Other");
        var suspended = await Harness.CreateTenantAsync("Suspended");
        var left = await Harness.CreateTenantAsync("Left");
        var subject = $"user-{Guid.NewGuid():N}";
        var admin = await Harness.AddAsync(mine, subject, "tenant-admin");
        await Harness.AddAsync(other, $"someone-{Guid.NewGuid():N}", "customer");
        await Harness.AddAsync(suspended, subject, "customer");
        await Harness.AddAsync(left, "boss", "tenant-admin");
        var leftMembership = await Harness.AddAsync(left, subject, "customer");
        await Harness.RemoveAsync(left, leftMembership.Id);
        await Harness.AddAsync(other, subject, "customer", TenantHarness.OtherIssuer);
        await Harness.SuspendTenantAsync(suspended);

        var memberships = await Harness.ListAsync(TenantHarness.Issuer, subject);

        var only = Assert.Single(memberships);
        Assert.Equal(admin.Id, only.MembershipId);
        Assert.Equal(mine, only.TenantId);
        Assert.Equal("tenant-admin", only.Role);
    }

    [Fact]
    public async Task Listing_matches_issuer_exactly_and_an_unknown_identity_has_no_memberships()
    {
        var tenant = await Harness.CreateTenantAsync();
        var subject = $"user-{Guid.NewGuid():N}";
        await Harness.AddAsync(tenant, subject, "customer", TenantHarness.OtherIssuer);

        Assert.Empty(await Harness.ListAsync(TenantHarness.Issuer, subject));
        Assert.Single(await Harness.ListAsync(TenantHarness.OtherIssuer, subject));
        Assert.Empty(await Harness.ListAsync(TenantHarness.Issuer, "nobody"));
    }

    [Fact]
    public async Task Resolving_a_tenant_context_reads_the_callers_active_membership_in_that_tenant_only()
    {
        var a = await Harness.CreateTenantAsync("A");
        var b = await Harness.CreateTenantAsync("B");
        var subject = $"user-{Guid.NewGuid():N}";
        var membership = await Harness.AddAsync(a, subject, "tenant-admin");
        await Harness.AddAsync(a, "boss", "tenant-admin");
        var caller = new ActorIdentity(TenantHarness.Issuer, subject);

        var role = await Harness.ResolveRoleAsync(a, caller, "members.manage");
        var otherTenant = await Assert.ThrowsAsync<AppException>(() => Harness.ResolveRoleAsync(b, caller, "tenant.read"));
        var otherIssuer = await Assert.ThrowsAsync<AppException>(() => Harness.ResolveRoleAsync(a, new ActorIdentity(TenantHarness.OtherIssuer, subject), "tenant.read"));
        await Harness.RemoveAsync(a, membership.Id);
        var removed = await Assert.ThrowsAsync<AppException>(() => Harness.ResolveRoleAsync(a, caller, "tenant.read"));

        Assert.Equal("tenant-admin", role);
        Assert.All([otherTenant, otherIssuer, removed], error => Assert.Equal("tenant.not_found", error.Error.Code));
    }

    [Fact]
    public async Task Resolving_a_suspended_tenant_is_forbidden_to_members_and_not_found_to_others()
    {
        var tenant = await Harness.CreateTenantAsync();
        var subject = $"user-{Guid.NewGuid():N}";
        await Harness.AddAsync(tenant, subject, "customer");
        await Harness.SuspendTenantAsync(tenant);

        var member = await Assert.ThrowsAsync<AppException>(() => Harness.ResolveRoleAsync(tenant, new ActorIdentity(TenantHarness.Issuer, subject), "tenant.read"));
        var stranger = await Assert.ThrowsAsync<AppException>(() => Harness.ResolveRoleAsync(tenant, new ActorIdentity(TenantHarness.Issuer, "nobody"), "tenant.read"));

        Assert.Equal("membership.tenant_suspended", member.Error.Code);
        Assert.Equal("tenant.not_found", stranger.Error.Code);
    }

    private static async Task<string?> Attempt(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (AppException exception)
        {
            return exception.Error.Code;
        }
    }

    private async Task<int> CountMembershipsAsync(Guid tenant) =>
        (int)await fixture.CountAsync("SELECT count(*) FROM identity_access.memberships WHERE tenant_id = @t", ("t", tenant));

    // Single-column reads. The membership id (or tenant id) parameter is bound by name when the statement uses it.
    private async Task<IReadOnlyList<string>> Rows(Guid tenant, string sql, Guid id)
    {
        var parameters = new List<(string, object)>();
        if (sql.Contains("@t", StringComparison.Ordinal))
        {
            parameters.Add(("t", tenant));
        }

        if (sql.Contains("@m", StringComparison.Ordinal))
        {
            parameters.Add(("m", id));
        }

        return await MigratorRun.QueryAsync<string>(fixture.Database.OwnerConnectionString, sql, [.. parameters]);
    }

    private Task<IReadOnlyList<string>> AuditAsync(Guid tenant) =>
        MigratorRun.QueryAsync<string>(
            fixture.Database.OwnerConnectionString,
            """
            SELECT action || '|' || actor_issuer || '|' || actor_subject || '|' || membership_id || '|' || coalesce(old_role, '') || '|' ||
                   coalesce(new_role, '') || '|' || to_char(occurred_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"0Z"')
            FROM identity_access.membership_audit WHERE tenant_id = @t ORDER BY id
            """,
            ("t", tenant));
}
