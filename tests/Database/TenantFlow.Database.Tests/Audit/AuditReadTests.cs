using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TenantFlow.Audit.Application.Records.Services;
using TenantFlow.Audit.Contracts;
using TenantFlow.Audit.Contracts.Events;
using TenantFlow.Audit.Infrastructure;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.Tenants.Infrastructure;

namespace TenantFlow.Database.Tests.Audit;

/// <summary>
/// SPEC-0009 criteria 4 and 5: reading the audit log through the real API host against real PostgreSQL. Records are made
/// by the Audit consumer with fixed times, so order and paging are exact. Who may call each route at all (anonymous,
/// customer, other tenants, removed members, suspended tenants) is the isolation matrix's job.
/// </summary>
public sealed class AuditReadTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    private WebApplicationFactory<Program>? _factory;
    private ServiceProvider? _services;

    private WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("Not initialized.");

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _factory = ApiFactory.CreateProduction(connectionString: fixture.Database.AppConnectionString);
        var services = new ServiceCollection();
        services.AddTenantsModule(fixture.Database.AppConnectionString);
        services.AddAuditModule(fixture.Database.AppConnectionString);
        _services = services.BuildServiceProvider();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    // ---- criterion 4 ----

    [Fact]
    public async Task An_administrator_reads_their_tenants_log_newest_first_page_by_page_and_never_another_tenants()
    {
        var (a, adminA) = await TenantWithAdministratorAsync("Reader A");
        var (b, adminB) = await TenantWithAdministratorAsync("Reader B");
        var oldest = await RecordAsync(a, AuditActions.TenantSuspended, AuditTargetTypes.Tenant, a, Start);
        var middle = await RecordAsync(a, AuditActions.TenantReactivated, AuditTargetTypes.Tenant, a, Start.AddMinutes(1));
        var newest = await RecordAsync(a, AuditActions.MembershipAdded, AuditTargetTypes.Membership, Guid.CreateVersion7(), Start.AddMinutes(2));
        var ofB = await RecordAsync(b, AuditActions.TenantSuspended, AuditTargetTypes.Tenant, b, Start.AddMinutes(3));

        var first = await ReadAsync($"/v1/tenants/{a}/audit?limit=2", TestIssuer.Shared.CreateToken(adminA));
        var second = await ReadAsync($"/v1/tenants/{a}/audit?limit=2&cursor={first.GetProperty("nextCursor").GetString()}", TestIssuer.Shared.CreateToken(adminA));
        var filtered = await ReadAsync($"/v1/tenants/{a}/audit?action={AuditActions.TenantSuspended}", TestIssuer.Shared.CreateToken(adminA));
        var forB = await ReadAsync($"/v1/tenants/{b}/audit", TestIssuer.Shared.CreateToken(adminB));

        Assert.Equal([newest, middle], Ids(first));
        Assert.Equal([oldest], Ids(second));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        Assert.Equal([oldest], Ids(filtered));
        Assert.Equal([ofB], Ids(forB));
        var record = first.GetProperty("items")[1];
        Assert.Equal(
            ["action", "actorKind", "actorRef", "id", "occurredAt", "targetId", "targetType", "traceId"],
            record.EnumerateObject().Select(static p => p.Name).Order());
        Assert.Equal((AuditActions.TenantReactivated, AuditActorKinds.PlatformAdmin, a, Start.AddMinutes(1)), (
            record.GetProperty("action").GetString(), record.GetProperty("actorKind").GetString(), record.GetProperty("targetId").GetGuid(),
            record.GetProperty("occurredAt").GetDateTimeOffset()));
    }

    [Theory]
    [InlineData("limit=0", "audit.limit_invalid")]
    [InlineData("limit=101", "audit.limit_invalid")]
    [InlineData("cursor=not-a-cursor", "audit.cursor_invalid")]
    [InlineData("action=tenant.deleted", "audit.action_invalid")]
    public async Task A_bad_page_request_is_refused_with_its_code(string query, string code)
    {
        var (a, adminA) = await TenantWithAdministratorAsync("Bad page");

        using var response = await SendAsync($"/v1/tenants/{a}/audit?{query}", TestIssuer.Shared.CreateToken(adminA));

        Assert.Equal((HttpStatusCode.BadRequest, code), (response.StatusCode, await PaymentScenario.CodeAsync(response)));
    }

    // ---- criterion 5 ----

    [Fact]
    public async Task A_platform_operator_reads_one_named_tenants_log_and_a_tenant_token_is_not_accepted()
    {
        var (a, adminA) = await TenantWithAdministratorAsync("Platform read");
        var (b, _) = await TenantWithAdministratorAsync("Platform other");
        var ofA = await RecordAsync(a, AuditActions.TenantSuspended, AuditTargetTypes.Tenant, a, Start);
        await RecordAsync(b, AuditActions.TenantSuspended, AuditTargetTypes.Tenant, b, Start);
        var operatorToken = TestIssuer.Platform.CreateOperatorToken("operator-audit-read");

        var read = await ReadAsync($"/v1/platform/audit?tenantId={a}", operatorToken);
        using var missing = await SendAsync("/v1/platform/audit", operatorToken);
        using var unknown = await SendAsync($"/v1/platform/audit?tenantId={Guid.CreateVersion7()}", operatorToken);
        using var tenantToken = await SendAsync($"/v1/platform/audit?tenantId={a}", TestIssuer.Shared.CreateToken(adminA));

        Assert.Equal([ofA], Ids(read));
        Assert.Equal((HttpStatusCode.BadRequest, "audit.tenant_required"), (missing.StatusCode, await PaymentScenario.CodeAsync(missing)));
        Assert.Equal((HttpStatusCode.NotFound, "tenant.not_found"), (unknown.StatusCode, await PaymentScenario.CodeAsync(unknown)));
        Assert.Equal(HttpStatusCode.Unauthorized, tenantToken.StatusCode);
    }

    // ---- helpers ----

    private async Task<(Guid Tenant, string Admin)> TenantWithAdministratorAsync(string name)
    {
        var tenant = await fixture.Harness.CreateTenantAsync(name);
        var admin = $"admin-{Guid.NewGuid():N}";
        await fixture.Harness.AddAsync(tenant, admin, "tenant-admin", TestIssuer.Issuer);
        return (tenant, admin);
    }

    // A record made the way the Worker makes one: an audit event handled by the consumer in a scope of its own.
    private async Task<Guid> RecordAsync(Guid tenant, string action, string targetType, Guid target, DateTimeOffset at)
    {
        var outgoing = OutgoingMessage.From(AuditRecordedV1.Of(action, TenantHarness.OperatorAudit, targetType, target, at));
        var message = new IncomingMessage(Guid.CreateVersion7(), outgoing.Type, outgoing.Version, tenant, outgoing.Payload, Guid.NewGuid().ToString("N"), null);
        await using var scope = _services!.CreateAsyncScope();
        Assert.Equal(MessageOutcome.Processed, await scope.ServiceProvider.GetRequiredService<AuditEventConsumer>().HandleAsync(message, Cancellation));
        return message.MessageId;
    }

    private static Guid[] Ids(JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(static r => r.GetProperty("id").GetGuid())];

    private async Task<JsonElement> ReadAsync(string path, string token)
    {
        using var response = await SendAsync(path, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await PaymentScenario.JsonAsync(response);
    }

    private async Task<HttpResponseMessage> SendAsync(string path, string token)
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, Cancellation);
    }
}
