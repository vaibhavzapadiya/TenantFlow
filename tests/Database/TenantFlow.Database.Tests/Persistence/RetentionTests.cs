using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TenantFlow.Audit.Infrastructure;
using TenantFlow.BuildingBlocks.Persistence;
using TenantFlow.BuildingBlocks.Persistence.Retention;
using TenantFlow.Catalog.Infrastructure;
using TenantFlow.IdentityAccess.Infrastructure;
using TenantFlow.ImportsExports.Infrastructure;
using TenantFlow.Notifications.Infrastructure;
using TenantFlow.Orders.Infrastructure;
using TenantFlow.Payments.Infrastructure;
using TenantFlow.Subscriptions.Infrastructure;
using TenantFlow.Tenants.Contracts;
using TenantFlow.Tenants.Infrastructure;

namespace TenantFlow.Database.Tests.Persistence;

/// <summary>
/// SPEC-0009 criteria 8 to 10 (ADR-0025): the module retention rules run tenant by tenant under row-level security, as the
/// application role, against real PostgreSQL. Each kind of row is inserted one second outside and one second inside its
/// window; another tenant's expired rows wait for that tenant's own run.
/// </summary>
public sealed class RetentionTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
{
    // The rules the Worker's retention task runs, composed the same way (WorkerHostTests pins that composition).
    private static readonly RetentionRule[] AllRules =
    [
        .. NotificationsRetention.Rules, .. OrdersRetention.Rules, .. PaymentsRetention.Rules, .. SubscriptionsRetention.Rules,
        .. CatalogRetention.Rules, .. ImportsExportsRetention.Rules, .. IdentityAccessRetention.Rules, .. TenantsRetention.Rules,
        .. AuditRetention.Rules,
    ];

    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private NpgsqlDataSource? _app;

    private NpgsqlDataSource App => _app ?? throw new InvalidOperationException("Not initialized.");

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _app = NpgsqlDataSource.Create(fixture.Database.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    // ---- criterion 8 ----

    [Fact]
    public async Task Expired_rows_go_a_row_one_second_inside_its_window_stays_and_another_tenant_waits_for_its_own_run()
    {
        var now = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        var a = await fixture.Harness.CreateTenantAsync("Retention A");
        var b = await fixture.Harness.CreateTenantAsync("Retention B");
        var kinds = Kinds();
        var expectations = new List<(string Kind, Guid Tenant, Guid Id, bool Expired)>();
        foreach (var (name, keep, insert, count) in kinds)
        {
            foreach (var (tenant, at, expired) in new[] { (a, now - keep - Second, true), (a, now - keep + Second, false), (b, now - keep - Second, true) })
            {
                var id = Guid.CreateVersion7();
                await AsOwnerAsync(tenant, insert, id, at);
                expectations.Add((name, tenant, id, expired));
            }
        }

        await TenantRetention.RunAsync(App, a, AllRules, now, Cancellation);
        var afterA = await PresentAsync(kinds, expectations);
        await TenantRetention.RunAsync(App, b, AllRules, now, Cancellation);
        var afterB = await PresentAsync(kinds, expectations);

        // A's expired rows are gone, its young ones stay, and B's expired rows are untouched until B's own run.
        Assert.Equal(expectations.Select(e => $"{e.Kind} {(e.Tenant == a ? "A" : "B")} {(e.Expired ? "expired" : "young")}: {!(e.Tenant == a && e.Expired)}"), afterA);
        Assert.Equal(expectations.Select(e => $"{e.Kind} {(e.Tenant == a ? "A" : "B")} {(e.Expired ? "expired" : "young")}: {!e.Expired}"), afterB);
    }

    [Fact]
    public async Task More_expired_rows_than_one_batch_are_all_deleted_in_bounded_batches()
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = await fixture.Harness.CreateTenantAsync("Retention batches");
        await AsOwnerAsync(
            tenant,
            """
            INSERT INTO orders.processed_messages (consumer, message_id, tenant_id, processed_at)
            SELECT 'retention-batch', gen_random_uuid(), @tenant, @at FROM generate_series(1, 1201)
            """,
            Guid.Empty,
            now - OrdersRetention.ProcessedMessages - Second);

        var changed = await TenantRetention.RunAsync(App, tenant, OrdersRetention.Rules, now, Cancellation);

        Assert.Equal(1201, changed);
        Assert.Equal(0L, await CountAsync(tenant, "SELECT count(*) FROM orders.processed_messages WHERE consumer = 'retention-batch'"));
    }

    [Fact]
    public async Task Every_tenant_is_listed_once_page_by_page_suspended_ones_included()
    {
        var created = new[] { await fixture.Harness.CreateTenantAsync("Paged 1"), await fixture.Harness.CreateTenantAsync("Paged 2"), await fixture.Harness.CreateTenantAsync("Paged 3") };
        await fixture.Harness.SuspendTenantAsync(created[1]);
        await using var provider = new ServiceCollection().AddTenantsModule(fixture.Database.AppConnectionString).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<ITenantDirectory>();

        var listed = new List<Guid>();
        Guid? after = null;
        while (await directory.ListIdsAsync(after, 2, Cancellation) is { Count: > 0 } page)
        {
            Assert.True(page.Count <= 2);
            listed.AddRange(page);
            after = page[^1];
        }

        Assert.Equal(listed.Order(), listed);
        Assert.Equal(listed.Count, listed.Distinct().Count());
        Assert.Superset(created.ToHashSet(), listed.ToHashSet());
    }

    // ---- criterion 9 ----

    [Fact]
    public async Task A_membership_removed_thirty_days_ago_loses_its_identity_keeps_its_row_and_the_person_can_return()
    {
        var harness = fixture.Harness;
        var tenant = await harness.CreateTenantAsync("Pseudonymised");
        var subject = $"leaver-{Guid.NewGuid():N}";
        await harness.AddAsync(tenant, $"admin-{Guid.NewGuid():N}", "tenant-admin");
        var leaver = await harness.AddAsync(tenant, subject, "customer");
        var stays = await harness.AddAsync(tenant, $"stays-{Guid.NewGuid():N}", "customer");
        await harness.RemoveAsync(tenant, leaver.Id);
        var removedAt = harness.Clock.GetUtcNow();

        await TenantRetention.RunAsync(App, tenant, IdentityAccessRetention.Rules, removedAt + IdentityAccessRetention.RemovedMemberships - Second, Cancellation);
        var inside = await IdentityAsync(tenant, leaver.Id);
        await TenantRetention.RunAsync(App, tenant, IdentityAccessRetention.Rules, removedAt + IdentityAccessRetention.RemovedMemberships + Second, Cancellation);
        var outside = await IdentityAsync(tenant, leaver.Id);
        var returned = await harness.AddAsync(tenant, subject, "customer");

        Assert.Equal((TenantHarness.Issuer, subject, "removed"), inside);
        Assert.Equal((IdentityAccessRetention.PseudonymIssuer, $"{IdentityAccessRetention.PseudonymIssuer}:{leaver.Id:D}", "removed"), outside);
        Assert.Equal((TenantHarness.Issuer, stays.Subject, "active"), await IdentityAsync(tenant, stays.Id));
        Assert.NotEqual(leaver.Id, returned.Id);
    }

    // ---- criterion 10 ----

    [Fact]
    public async Task Audit_rows_older_than_four_hundred_days_leave_through_their_purge_functions_and_younger_ones_stay()
    {
        var tenant = await fixture.Harness.CreateTenantAsync("Audit purge");
        var other = await fixture.Harness.CreateTenantAsync("Audit purge other");
        var minute = TimeSpan.FromMinutes(1);
        // The functions compare with the database clock (a fixed cutoff no caller can move), so the margin is a minute.
        var old = DateTimeOffset.UtcNow - AuditRetention.AuditRecords - minute;
        var young = DateTimeOffset.UtcNow - AuditRetention.AuditRecords + minute;
        foreach (var (owner, at) in new[] { (tenant, old), (tenant, young), (other, old) })
        {
            await AsOwnerAsync(owner, InsertAuditRecord, Guid.CreateVersion7(), at);
            await AsOwnerAsync(owner, InsertTenantAudit, Guid.Empty, at);
            await AsOwnerAsync(owner, InsertMembershipAudit, Guid.CreateVersion7(), at);
        }

        await TenantRetention.RunAsync(App, tenant, AllRules, DateTimeOffset.UtcNow, Cancellation);

        // Only the rows this test wrote: creating the tenants wrote audit rows of their own.
        foreach (var (table, probe) in new[] { ("audit.audit_records", "actor_kind = 'platform_admin'"), ("tenants.tenant_audit", "actor_subject = 'retention-probe'"), ("identity_access.membership_audit", "actor_subject = 'retention-probe'") })
        {
            Assert.Equal((1L, 0L), (await CountAsync(tenant, $"SELECT count(*) FROM {table} WHERE tenant_id = @tenant AND {probe} AND occurred_at > @at", young - minute),
                await CountAsync(tenant, $"SELECT count(*) FROM {table} WHERE tenant_id = @tenant AND {probe} AND occurred_at < @at", young - minute)));
            Assert.Equal(1L, await CountAsync(other, $"SELECT count(*) FROM {table} WHERE tenant_id = @tenant AND {probe} AND occurred_at < @at", young - minute));
        }
    }

    // ---- helpers ----

    private const string InsertAuditRecord = """
        INSERT INTO audit.audit_records (id, tenant_id, occurred_at, actor_kind, actor_ref, action, target_type, target_id)
        VALUES (@id, @tenant, @at, 'platform_admin', repeat('0', 64), 'tenant.suspended', 'tenant', @tenant)
        """;

    private const string InsertTenantAudit = """
        INSERT INTO tenants.tenant_audit (tenant_id, action, actor_issuer, actor_subject, old_status, new_status, occurred_at)
        VALUES (@tenant, 'tenant.suspended', 'https://idp.test/realms/tenantflow-platform', 'retention-probe', 'active', 'suspended', @at)
        """;

    private const string InsertMembershipAudit = """
        INSERT INTO identity_access.membership_audit (tenant_id, membership_id, action, actor_issuer, actor_subject, old_role, new_role, occurred_at)
        VALUES (@tenant, @id, 'membership.added', 'https://idp.test/realms/tenantflow-platform', 'retention-probe', NULL, 'customer', @at)
        """;

    private sealed record Kind(string Name, TimeSpan Keep, string Insert, string Count);

    // One row per kind of rule. Each insert writes a row whose age is @at, identified by @id; each count finds it again,
    // or any part of it that a rule left behind.
    private static List<Kind> Kinds()
    {
        var kinds = new List<Kind>();
        foreach (var schema in new[] { "orders", "notifications", "audit" })
        {
            kinds.Add(new($"{schema}.processed_messages", OrdersRetention.ProcessedMessages,
                $"INSERT INTO {schema}.processed_messages (consumer, message_id, tenant_id, processed_at) VALUES ('retention', @id, @tenant, @at)",
                $"SELECT count(*) FROM {schema}.processed_messages WHERE message_id = @id"));
        }

        foreach (var schema in new[] { "orders", "payments", "subscriptions", "imports_exports" })
        {
            kinds.Add(new($"{schema}.completed_timers", OrdersRetention.CompletedTimers,
                $"INSERT INTO {schema}.workflow_timers (id, tenant_id, kind, subject_id, due_at, completed_at) VALUES (@id, @tenant, 'retention', @id, @at, @at)",
                $"SELECT count(*) FROM {schema}.workflow_timers WHERE id = @id"));
            kinds.Add(new($"{schema}.dead_timers", OrdersRetention.DeadTimers,
                $"INSERT INTO {schema}.workflow_timers (id, tenant_id, kind, subject_id, due_at, dead_at) VALUES (@id, @tenant, 'retention', @id, @at, @at)",
                $"SELECT count(*) FROM {schema}.workflow_timers WHERE id = @id"));
        }

        kinds.Add(new("payments.webhook_events", PaymentsRetention.WebhookEvents,
            "INSERT INTO payments.webhook_events (provider, event_id, tenant_id, payment_id, outcome, received_at) VALUES ('retention', @id::text, @tenant, @id, 'applied', @at)",
            "SELECT count(*) FROM payments.webhook_events WHERE event_id = @id::text"));
        kinds.Add(new("payments.idempotency_keys", PaymentsRetention.FinishedIdempotencyKeys,
            "INSERT INTO payments.idempotency_keys (tenant_id, operation, key, request_hash, payment_id, created_at, completed_at) VALUES (@tenant, 'retention', @id::text, repeat('0', 64), @id, @at, @at)",
            "SELECT count(*) FROM payments.idempotency_keys WHERE key = @id::text"));
        kinds.Add(new("catalog.import_batches", CatalogRetention.ImportBatches,
            "INSERT INTO catalog.import_batches (tenant_id, job_id, batch_number, result, applied_at) VALUES (@tenant, @id, 1, '{}', @at)",
            "SELECT count(*) FROM catalog.import_batches WHERE job_id = @id"));
        // A finished import with a row error and its file row, and a finished export with its file row: all go together.
        kinds.Add(new("imports_exports.import_job", ImportsExportsRetention.FinishedJobs,
            """
            INSERT INTO imports_exports.stored_files (id, tenant_id, kind, key, content_type, created_at) VALUES (@id, @tenant, 'import_upload', @id::text, 'text/csv', @at);
            INSERT INTO imports_exports.import_jobs (id, tenant_id, status, file_id, idempotency_key, request_hash, checkpoint_row, created_count, updated_count, failed_count, requested_by, created_at, updated_at, completed_at)
            VALUES (@id, @tenant, 'succeeded', @id, @id::text, repeat('0', 64), 0, 0, 0, 1, @id, @at, @at, @at);
            INSERT INTO imports_exports.import_row_errors (tenant_id, job_id, "row", "column", code) VALUES (@tenant, @id, 2, 'sku', 'retention');
            """,
            """
            SELECT (SELECT count(*) FROM imports_exports.import_jobs WHERE id = @id) + (SELECT count(*) FROM imports_exports.import_row_errors WHERE job_id = @id)
                 + (SELECT count(*) FROM imports_exports.stored_files WHERE id = @id)
            """));
        kinds.Add(new("imports_exports.export_job", ImportsExportsRetention.FinishedJobs,
            """
            INSERT INTO imports_exports.stored_files (id, tenant_id, kind, key, content_type, created_at) VALUES (@id, @tenant, 'export', @id::text, 'text/csv', @at);
            INSERT INTO imports_exports.export_jobs (id, tenant_id, status, file_id, idempotency_key, request_hash, row_count, requested_by, created_at, updated_at, completed_at)
            VALUES (@id, @tenant, 'succeeded', @id, @id::text, repeat('0', 64), 0, @id, @at, @at, @at);
            """,
            "SELECT (SELECT count(*) FROM imports_exports.export_jobs WHERE id = @id) + (SELECT count(*) FROM imports_exports.stored_files WHERE id = @id)"));
        return kinds;
    }

    // "kind tenant age: present" for every expected row, in insertion order.
    private async Task<List<string>> PresentAsync(List<Kind> kinds, List<(string Kind, Guid Tenant, Guid Id, bool Expired)> expectations)
    {
        var lines = new List<string>();
        foreach (var e in expectations)
        {
            var kind = kinds.Single(k => k.Name == e.Kind);
            var present = await CountAsync(e.Tenant, kind.Count, e.Id) > 0;
            lines.Add($"{e.Kind} {(e.Tenant == expectations[0].Tenant ? "A" : "B")} {(e.Expired ? "expired" : "young")}: {present}");
        }

        return lines;
    }

    // As the owner, inside the tenant's row-level security scope, so a forced policy's WITH CHECK holds.
    private async Task AsOwnerAsync(Guid tenant, string sql, Guid id, DateTimeOffset at)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.OwnerConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var transaction = await connection.BeginTransactionAsync(Cancellation);
        await using (var scope = new NpgsqlCommand($"SELECT set_config('{TenantScopedTransaction.SettingName}', @tenant, true)", connection, transaction))
        {
            scope.Parameters.AddWithValue("tenant", tenant.ToString("D"));
            await scope.ExecuteNonQueryAsync(Cancellation);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("at", at);
        await command.ExecuteNonQueryAsync(Cancellation);
        await transaction.CommitAsync(Cancellation);
    }

    // As the owner in the tenant's scope: the application role cannot read every table, and RLS still applies.
    private async Task<long> CountAsync(Guid tenant, string sql, object? value = null)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.OwnerConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var transaction = await connection.BeginTransactionAsync(Cancellation);
        await using (var scope = new NpgsqlCommand($"SELECT set_config('{TenantScopedTransaction.SettingName}', @tenant, true)", connection, transaction))
        {
            scope.Parameters.AddWithValue("tenant", tenant.ToString("D"));
            await scope.ExecuteNonQueryAsync(Cancellation);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenant);
        if (value is Guid id)
        {
            command.Parameters.AddWithValue("id", id);
        }
        else if (value is DateTimeOffset at)
        {
            command.Parameters.AddWithValue("at", at);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(Cancellation), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<(string Issuer, string Subject, string Status)> IdentityAsync(Guid tenant, Guid membership)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = new NpgsqlCommand("SELECT issuer, subject, status FROM identity_access.memberships WHERE tenant_id = @tenant AND id = @id", connection);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("id", membership);
        await using var reader = await command.ExecuteReaderAsync(Cancellation);
        Assert.True(await reader.ReadAsync(Cancellation));
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }
}
