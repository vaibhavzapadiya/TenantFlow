using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TenantFlow.Audit.Application.Records.Services;
using TenantFlow.Audit.Contracts;
using TenantFlow.Audit.Contracts.Events;
using TenantFlow.Audit.Infrastructure;
using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.BuildingBlocks.Messaging.Outbox;
using TenantFlow.BuildingBlocks.Messaging.RabbitMq;
using TenantFlow.BuildingBlocks.Persistence;
using TenantFlow.Tenants.Application.Abstractions;
using TenantFlow.Tenants.Domain.Auditing;
using TenantFlow.Tenants.Domain.Tenants;
using TenantFlow.Tenants.Infrastructure;

namespace TenantFlow.Database.Tests.Audit;

/// <summary>
/// SPEC-0009 criteria 2, 3 and 6 (ADR-0026): Tenants and IdentityAccess write an <c>audit.recorded.v1</c> event to their
/// own outbox in the transaction of the change, and the Audit consumer turns each into exactly one append-only record in
/// the event's tenant. Against real PostgreSQL, the real API host and the local RabbitMQ.
/// </summary>
public sealed class AuditLogTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
{
    private const string OperatorSubject = "operator-audit";
    private const string TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(15);

    private WebApplicationFactory<Program>? _factory;
    private ServiceProvider? _services;
    private NpgsqlDataSource? _dataSource;

    private WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("Not initialized.");

    private ServiceProvider Services => _services ?? throw new InvalidOperationException("Not initialized.");

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _factory = ApiFactory.CreateProduction(connectionString: fixture.Database.AppConnectionString);
        var services = new ServiceCollection();
        services.AddTenantsModule(fixture.Database.AppConnectionString);
        services.AddAuditModule(fixture.Database.AppConnectionString);
        _services = services.BuildServiceProvider();
        _dataSource = NpgsqlDataSource.Create(fixture.Database.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    private sealed record OutboxEvent(Guid Id, string Type, int Version, JsonElement Payload, string? TraceParent)
    {
        public string Action => Payload.GetProperty("action").GetString()!;

        public string ActorKind => Payload.GetProperty("actorKind").GetString()!;

        public string ActorRef => Payload.GetProperty("actorRef").GetString()!;

        public Guid TargetId => Payload.GetProperty("targetId").GetGuid();
    }

    private sealed record AuditRow(Guid Id, Guid TenantId, string ActorKind, string ActorRef, string Action, string TargetType, Guid TargetId, string? TraceId);

    // ---- criterion 2: the write side ----

    [Fact]
    public async Task Each_platform_and_member_change_writes_one_audit_event_naming_its_actor_by_reference_only()
    {
        var operatorToken = TestIssuer.Platform.CreateOperatorToken(OperatorSubject);
        var adminSubject = $"admin-{Guid.NewGuid():N}";
        var memberSubject = $"member-{Guid.NewGuid():N}";

        using var created = await SendAsync(HttpMethod.Post, "/v1/platform/tenants", operatorToken, new { name = $"Audited {Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var tenant = (await created.Content.ReadFromJsonAsync<JsonElement>(Cancellation)).GetProperty("id").GetGuid();
        using var admin = await SendAsync(HttpMethod.Post, $"/v1/platform/tenants/{tenant}/admins", operatorToken, new { subject = adminSubject });
        Assert.Equal(HttpStatusCode.Created, admin.StatusCode);
        var adminMembership = (await admin.Content.ReadFromJsonAsync<JsonElement>(Cancellation)).GetProperty("id").GetGuid();
        using var suspended = await SendAsync(HttpMethod.Post, $"/v1/platform/tenants/{tenant}/suspend", operatorToken);
        using var reactivated = await SendAsync(HttpMethod.Post, $"/v1/platform/tenants/{tenant}/reactivate", operatorToken);
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (suspended.StatusCode, reactivated.StatusCode));

        var adminToken = TestIssuer.Shared.CreateToken(adminSubject);
        using var added = await SendAsync(HttpMethod.Post, $"/v1/tenants/{tenant}/members", adminToken, new { subject = memberSubject, role = "customer" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var member = (await added.Content.ReadFromJsonAsync<JsonElement>(Cancellation)).GetProperty("id").GetGuid();
        using var promoted = await SendAsync(HttpMethod.Put, $"/v1/tenants/{tenant}/members/{member}/role", adminToken, new { role = "tenant-admin" });
        using var removed = await SendAsync(HttpMethod.Delete, $"/v1/tenants/{tenant}/members/{member}", adminToken);
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.NoContent), (promoted.StatusCode, removed.StatusCode));

        var tenantEvents = await OutboxEventsAsync("tenants", tenant);
        var membershipEvents = await OutboxEventsAsync("identity_access", tenant);

        var operatorRef = OperatorReference(TestIssuer.PlatformIssuer, OperatorSubject);
        Assert.Equal(
            [(AuditActions.TenantCreated, tenant), (AuditActions.TenantSuspended, tenant), (AuditActions.TenantReactivated, tenant)],
            tenantEvents.Select(static e => (e.Action, e.TargetId)));
        Assert.All(tenantEvents, e => Assert.Equal((AuditActorKinds.PlatformAdmin, operatorRef), (e.ActorKind, e.ActorRef)));
        Assert.Equal(
            [
                (AuditActions.MembershipAdded, AuditActorKinds.PlatformAdmin, operatorRef, adminMembership),
                (AuditActions.MembershipAdded, AuditActorKinds.Member, adminMembership.ToString("D"), member),
                (AuditActions.MembershipRoleChanged, AuditActorKinds.Member, adminMembership.ToString("D"), member),
                (AuditActions.MembershipRemoved, AuditActorKinds.Member, adminMembership.ToString("D"), member),
            ],
            membershipEvents.Select(static e => (e.Action, e.ActorKind, e.ActorRef, e.TargetId)));

        // Codes and ids only: no subject, issuer, name or free text anywhere in the event.
        foreach (var e in tenantEvents.Concat(membershipEvents))
        {
            Assert.Equal((AuditRecordedV1.EventType, 1), (e.Type, e.Version));
            Assert.Equal(["action", "actorKind", "actorRef", "occurredAt", "targetId", "targetType"], e.Payload.EnumerateObject().Select(static p => p.Name).Order());
            var text = e.Payload.GetRawText();
            Assert.DoesNotContain(OperatorSubject, text, StringComparison.Ordinal);
            Assert.DoesNotContain(adminSubject, text, StringComparison.Ordinal);
            Assert.DoesNotContain(memberSubject, text, StringComparison.Ordinal);
            Assert.DoesNotContain("issuer.tenantflow.test", text, StringComparison.Ordinal);
            Assert.Matches("^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$", e.TraceParent);
        }
    }

    [Fact]
    public async Task A_refused_change_writes_no_audit_event()
    {
        var tenant = await fixture.Harness.CreateTenantAsync("Refused");
        var admin = await fixture.Harness.AddAsync(tenant, $"admin-{Guid.NewGuid():N}", "tenant-admin", TestIssuer.Issuer);
        var before = (await OutboxEventsAsync("identity_access", tenant)).Count;

        // The last administrator cannot leave; the refusal rolls back the change and its event together.
        using var refused = await SendAsync(HttpMethod.Delete, $"/v1/tenants/{tenant}/members/{admin.Id}", TestIssuer.Shared.CreateToken(admin.Subject));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(before, (await OutboxEventsAsync("identity_access", tenant)).Count);
    }

    [Fact]
    public async Task When_the_change_fails_at_commit_its_audit_event_rolls_back_with_it()
    {
        var existing = await fixture.Harness.CreateTenantAsync("Taken name");
        await using var scope = Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ITenantStore>();
        var name = (await store.FindAsync(existing, Cancellation))!.Name;
        var now = DateTimeOffset.UtcNow;
        var duplicate = Tenant.Create(Guid.CreateVersion7(now), name, now);

        // The event is added first and the change second, so a commit that wrote the event on its own would keep it.
        // The name is taken, so only the database refuses the change, at commit, after both were added.
        store.Announce(OutgoingMessage.From(TenantAuditEvents.Created(duplicate, TenantHarness.OperatorAudit, now)), duplicate.Id, now);
        store.Add(duplicate);
        var refused = await Assert.ThrowsAsync<AppException>(() => store.SaveChangesAsync(Cancellation));

        Assert.Equal(TenantErrors.NameTaken, refused.Error);
        Assert.Empty(await OutboxEventsAsync("tenants", duplicate.Id));
    }

    // ---- criteria 2 and 6: the consumer ----

    [Fact]
    public async Task Every_audit_event_made_while_the_broker_was_down_is_recorded_once_after_it_recovers()
    {
        var tenant = await fixture.Harness.CreateTenantAsync("Broker down");
        await fixture.Harness.SuspendTenantAsync(tenant);
        var events = await OutboxEventsAsync("tenants", tenant);
        Assert.Equal(2, events.Count);

        await using var broker = await TestBroker.CreateAsync(Cancellation);
        var outcomes = Channel.CreateUnbounded<(Guid Tenant, MessageOutcome Outcome)>();
        await using var connection = new RabbitMqConnection(Options.Create(broker.Options));
        await using var consumer = new RabbitMqConsumer(connection, broker.NewQueueName(), AuditEventConsumer.EventTypes, Recording(outcomes), NullLogger.Instance);
        await consumer.StartAsync(Cancellation);
        using var metrics = new OutboxMetrics(TimeProvider.System);
        await using var down = new RabbitMqMessageBus(new RabbitMqConnection(Options.Create(BrokerTestSettings.Unreachable(broker.Options))));
        await using var up = new RabbitMqMessageBus(new RabbitMqConnection(Options.Create(broker.Options)));

        // The changes committed while the broker was down; their events wait in the outbox, then go out once it is back.
        Assert.Equal(0, await Publisher(down, metrics).PublishPendingAsync(Cancellation));
        Assert.Empty(await RecordsAsync(tenant));
        var published = 0;
        var deadline = DateTimeOffset.UtcNow + DeliveryTimeout;
        while ((await RecordsAsync(tenant)).Count < events.Count)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The audit events were never recorded.");
            published += await Publisher(up, metrics).PublishPendingAsync(Cancellation);
            await Task.Delay(TimeSpan.FromMilliseconds(200), Cancellation);
        }

        Assert.True(published >= events.Count);
        var records = await RecordsAsync(tenant);
        Assert.Equal(events.Select(static e => e.Id).Order(), records.Select(static r => r.Id).Order());
        Assert.Equal([AuditActions.TenantCreated, AuditActions.TenantSuspended], records.OrderBy(static r => r.Action == AuditActions.TenantSuspended).Select(static r => r.Action));
        Assert.All(records, r => Assert.Equal((tenant, AuditActorKinds.PlatformAdmin, AuditTargetTypes.Tenant, tenant), (r.TenantId, r.ActorKind, r.TargetType, r.TargetId)));
    }

    [Fact]
    public async Task A_message_delivered_twice_and_concurrently_makes_one_record_with_the_message_id_and_trace()
    {
        var tenant = await fixture.Harness.CreateTenantAsync("Duplicates");
        var message = Incoming(tenant, AuditRecordedV1.Of(AuditActions.TenantSuspended, TenantHarness.OperatorAudit, AuditTargetTypes.Tenant, tenant, DateTimeOffset.UtcNow));

        var concurrent = await Task.WhenAll(HandleAsync(message), HandleAsync(message));
        var again = await HandleAsync(message);

        Assert.Equal([MessageOutcome.Processed, MessageOutcome.Duplicate], concurrent.Order());
        Assert.Equal(MessageOutcome.Duplicate, again);
        var record = Assert.Single(await RecordsAsync(tenant), r => r.Id == message.MessageId);
        Assert.Equal((AuditActions.TenantSuspended, "0af7651916cd43dd8448eb211c80319c"), (record.Action, record.TraceId));
        Assert.Equal(1L, await ProcessedCountAsync(tenant, message.MessageId));
    }

    [Fact]
    public async Task Each_message_is_recorded_in_its_own_tenant_and_another_tenant_never_sees_it()
    {
        var tenantA = await fixture.Harness.CreateTenantAsync("Tenant A");
        var tenantB = await fixture.Harness.CreateTenantAsync("Tenant B");
        var forB = Incoming(tenantB, AuditRecordedV1.Of(AuditActions.TenantSuspended, TenantHarness.OperatorAudit, AuditTargetTypes.Tenant, tenantB, DateTimeOffset.UtcNow));
        var forA = Incoming(tenantA, AuditRecordedV1.Of(AuditActions.TenantSuspended, TenantHarness.OperatorAudit, AuditTargetTypes.Tenant, tenantA, DateTimeOffset.UtcNow));

        // B first, then A, through the same path the Worker uses: nothing of B's carries over to A.
        Assert.Equal(MessageOutcome.Processed, await HandleAsync(forB));
        Assert.Equal(MessageOutcome.Processed, await HandleAsync(forA));

        Assert.Equal([forA.MessageId], (await RecordsAsync(tenantA)).Select(static r => r.Id));
        Assert.Equal([forB.MessageId], (await RecordsAsync(tenantB)).Select(static r => r.Id));

        // Row-level security: scoped to A, the application role neither sees B's record nor writes one for B.
        await using var connection = await TenantScopedConnectionAsync(tenantA);
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM audit.audit_records WHERE id = @id", connection.Connection, connection.Transaction))
        {
            count.Parameters.AddWithValue("id", forB.MessageId);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(Cancellation))!);
        }

        await using var forge = new NpgsqlCommand(
            """
            INSERT INTO audit.audit_records (id, tenant_id, occurred_at, actor_kind, actor_ref, action, target_type, target_id)
            VALUES (@id, @tenant, now(), 'service', 'forger', 'tenant.suspended', 'tenant', @tenant)
            """,
            connection.Connection,
            connection.Transaction);
        forge.Parameters.AddWithValue("id", Guid.NewGuid());
        forge.Parameters.AddWithValue("tenant", tenantB);
        var refused = await Assert.ThrowsAsync<PostgresException>(() => forge.ExecuteNonQueryAsync(Cancellation));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    [Theory]
    [InlineData("unknown-tenant")]
    [InlineData("subject-as-member-reference")]
    [InlineData("subject-as-operator-reference")]
    [InlineData("unknown-action")]
    [InlineData("unknown-target-type")]
    [InlineData("free-text-payload")]
    [InlineData("another-tenant-as-target")]
    [InlineData("missing-occurred-at")]
    [InlineData("non-utc-occurred-at")]
    public async Task An_event_that_can_never_be_recorded_is_refused_for_the_dead_letter_queue_and_records_nothing(string shape)
    {
        var tenant = await fixture.Harness.CreateTenantAsync("Refused events");
        var envelopeTenant = shape == "unknown-tenant" ? Guid.NewGuid() : tenant;
        var reference = new string('0', 64);
        // A tenant target is the envelope's tenant, so each shape is refused for its own reason only.
        var payload = shape switch
        {
            "subject-as-member-reference" => Payload("tenant.suspended", "member", "user@example.test", "tenant", envelopeTenant),
            "subject-as-operator-reference" => Payload("tenant.suspended", "platform_admin", "operator-1", "tenant", envelopeTenant),
            "unknown-action" => Payload("tenant.deleted", "platform_admin", reference, "tenant", envelopeTenant),
            "unknown-target-type" => Payload("tenant.suspended", "platform_admin", reference, "invoice", envelopeTenant),
            "free-text-payload" => "\"a note about a person\"",
            // An existing tenant, so only the rule that a tenant's log names no other tenant can refuse it.
            "another-tenant-as-target" => Payload("tenant.suspended", "platform_admin", reference, "tenant", await fixture.Harness.CreateTenantAsync("Other target")),
            "missing-occurred-at" => JsonSerializer.Serialize(new { action = "tenant.suspended", actorKind = "platform_admin", actorRef = reference, targetType = "tenant", targetId = envelopeTenant }),
            "non-utc-occurred-at" => Payload("tenant.suspended", "platform_admin", reference, "tenant", envelopeTenant, DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(2))),
            _ => Payload("tenant.suspended", "platform_admin", reference, "tenant", envelopeTenant),
        };
        var message = new IncomingMessage(Guid.CreateVersion7(), AuditRecordedV1.EventType, 1, envelopeTenant, payload, "correlation", null);

        var refusal = await Assert.ThrowsAsync<InvalidMessageException>(() => HandleAsync(message));

        // The reason never quotes the payload.
        Assert.DoesNotContain("example.test", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(await RecordsAsync(tenant));
        Assert.Empty(await RecordsAsync(envelopeTenant));
    }

    // ---- criterion 3 ----

    [Fact]
    public async Task The_application_role_can_only_add_and_read_audit_records_and_no_role_can_change_one()
    {
        var tenant = await fixture.Harness.CreateTenantAsync("Append only");
        var message = Incoming(tenant, AuditRecordedV1.Of(AuditActions.TenantSuspended, TenantHarness.OperatorAudit, AuditTargetTypes.Tenant, tenant, DateTimeOffset.UtcNow));
        Assert.Equal(MessageOutcome.Processed, await HandleAsync(message));

        var privileges = await MigratorRun.QueryAsync<bool>(
            fixture.Database.AppConnectionString,
            "SELECT has_table_privilege(current_user, 'audit.audit_records', p) FROM unnest(ARRAY['INSERT', 'SELECT', 'UPDATE', 'DELETE', 'TRUNCATE']) AS p");
        Assert.Equal([true, true, false, false, false], privileges);

        // Even as the application role inside the record's own tenant, a change is refused by privileges.
        await using (var scoped = await TenantScopedConnectionAsync(tenant))
        {
            foreach (var sql in new[] { "UPDATE audit.audit_records SET action = 'tenant.created' WHERE id = @id", "DELETE FROM audit.audit_records WHERE id = @id" })
            {
                await using var command = new NpgsqlCommand($"SAVEPOINT s; {sql}", scoped.Connection, scoped.Transaction);
                command.Parameters.AddWithValue("id", message.MessageId);
                var refused = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Cancellation));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
                await using var rollback = new NpgsqlCommand("ROLLBACK TO SAVEPOINT s", scoped.Connection, scoped.Transaction);
                await rollback.ExecuteNonQueryAsync(Cancellation);
            }
        }

        // The owner role is refused by the trigger: no role can rewrite history.
        // Inside the tenant's scope, because forced row-level security applies to the owner as well: without it the update
        // would match no row and prove nothing.
        await using var owner = new NpgsqlConnection(fixture.Database.OwnerConnectionString);
        await owner.OpenAsync(Cancellation);
        await using var ownerTransaction = await owner.BeginTransactionAsync(Cancellation);
        await using var update = new NpgsqlCommand(
            $"SELECT set_config('{TenantScopedTransaction.SettingName}', @tenant, true); UPDATE audit.audit_records SET action = 'tenant.created' WHERE id = @id",
            owner,
            ownerTransaction);
        update.Parameters.AddWithValue("tenant", tenant.ToString());
        update.Parameters.AddWithValue("id", message.MessageId);
        var ownerRefused = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync(Cancellation));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ownerRefused.SqlState);
        Assert.Equal(AuditActions.TenantSuspended, Assert.Single(await RecordsAsync(tenant)).Action);
    }

    // ---- helpers ----

    private static string Payload(string action, string actorKind, string actorRef, string targetType, Guid targetId, DateTimeOffset? occurredAt = null) =>
        JsonSerializer.Serialize(new { action, actorKind, actorRef, targetType, targetId, occurredAt = occurredAt ?? DateTimeOffset.UtcNow });

    private static IncomingMessage Incoming(Guid tenant, AuditRecordedV1 auditEvent)
    {
        var outgoing = OutgoingMessage.From(auditEvent);
        return new IncomingMessage(Guid.CreateVersion7(), outgoing.Type, outgoing.Version, tenant, outgoing.Payload, Guid.NewGuid().ToString("N"), TraceParent);
    }

    // The reference the API computes for a platform operator: HMAC-SHA256 of issuer, line feed and subject (ADR-0026).
    private static string OperatorReference(string issuer, string subject) =>
        Convert.ToHexString(HMACSHA256.HashData(Convert.FromBase64String(ApiFactory.OperatorReferenceKey), Encoding.UTF8.GetBytes(issuer + "\n" + subject))).ToLowerInvariant();

    private OutboxPublisher Publisher(IMessageBus bus, OutboxMetrics metrics) =>
        new(
            [new OutboxStore(_dataSource ?? throw new InvalidOperationException("Not initialized."), "tenants")],
            bus,
            metrics,
            TimeProvider.System,
            Options.Create(new OutboxOptions()),
            NullLogger<OutboxPublisher>.Instance);

    // As the Worker does it: a fresh scope per message.
    private async Task<MessageOutcome> HandleAsync(IncomingMessage message)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuditEventConsumer>().HandleAsync(message, Cancellation);
    }

    private Func<IncomingMessage, CancellationToken, Task<MessageOutcome>> Recording(Channel<(Guid, MessageOutcome)> outcomes) =>
        async (message, cancellationToken) =>
        {
            await using var scope = Services.CreateAsyncScope();
            var outcome = await scope.ServiceProvider.GetRequiredService<AuditEventConsumer>().HandleAsync(message, cancellationToken);
            await outcomes.Writer.WriteAsync((message.TenantId, outcome), cancellationToken);
            return outcome;
        };

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object? body = null)
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, Cancellation);
    }

    // The tenant's audit events in a module's outbox, oldest first. Outbox rows are outside row-level security (ADR-0021).
    private async Task<List<OutboxEvent>> OutboxEventsAsync(string schema, Guid tenant)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = new NpgsqlCommand(
            $"SELECT id, type, version, payload::text, traceparent FROM {schema}.outbox_messages WHERE tenant_id = @tenant AND type = @type ORDER BY occurred_at, id",
            connection);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("type", AuditRecordedV1.EventType);
        await using var reader = await command.ExecuteReaderAsync(Cancellation);
        var events = new List<OutboxEvent>();
        while (await reader.ReadAsync(Cancellation))
        {
            events.Add(new OutboxEvent(
                reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), JsonDocument.Parse(reader.GetString(3)).RootElement.Clone(), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return events;
    }

    // The tenant's audit records, read as the application role inside the tenant's scope.
    private async Task<List<AuditRow>> RecordsAsync(Guid tenant)
    {
        await using var scoped = await TenantScopedConnectionAsync(tenant);
        await using var command = new NpgsqlCommand(
            "SELECT id, tenant_id, actor_kind, actor_ref, action, target_type, target_id, trace_id FROM audit.audit_records ORDER BY occurred_at, id",
            scoped.Connection,
            scoped.Transaction);
        await using var reader = await command.ExecuteReaderAsync(Cancellation);
        var records = new List<AuditRow>();
        while (await reader.ReadAsync(Cancellation))
        {
            records.Add(new AuditRow(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetGuid(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return records;
    }

    private async Task<long> ProcessedCountAsync(Guid tenant, Guid messageId)
    {
        await using var scoped = await TenantScopedConnectionAsync(tenant);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM audit.processed_messages WHERE message_id = @id", scoped.Connection, scoped.Transaction);
        command.Parameters.AddWithValue("id", messageId);
        return (long)(await command.ExecuteScalarAsync(Cancellation))!;
    }

    private async Task<ScopedConnection> TenantScopedConnectionAsync(Guid tenant)
    {
        var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync(Cancellation);
        var transaction = await connection.BeginTransactionAsync(Cancellation);
        await using var set = new NpgsqlCommand($"SELECT set_config('{TenantScopedTransaction.SettingName}', @tenant, true)", connection, transaction);
        set.Parameters.AddWithValue("tenant", tenant.ToString());
        await set.ExecuteNonQueryAsync(Cancellation);
        return new ScopedConnection(connection, transaction);
    }

    private sealed class ScopedConnection(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public NpgsqlConnection Connection { get; } = connection;

        public NpgsqlTransaction Transaction { get; } = transaction;

        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
