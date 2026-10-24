using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TenantFlow.Api.Extensions;

namespace TenantFlow.Database.Tests.Operations;

/// <summary>
/// SPEC-0009 criteria 15 to 18 (ADR-0027) through the real API host against real PostgreSQL: the platform route matrix,
/// support grants, the read-only support views, and ending, revoking and expiring a grant.
/// </summary>
public sealed class SupportAccessTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Start);
    private WebApplicationFactory<Program>? _factory;

    private WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("Not initialized.");

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _factory = ApiFactory.CreateProduction(
            services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
            },
            connectionString: fixture.Database.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    // ---- criterion 15 ----

    [Fact]
    public async Task Every_platform_route_answers_each_kind_of_caller_as_its_role_says()
    {
        var routes = Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(static r => r.RoutePattern.RawText!.StartsWith("/v1/platform/", StringComparison.Ordinal))
            .SelectMany(static r => (r.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? []).Select(method =>
                (Method: method, Pattern: r.RoutePattern.RawText!, Policy: r.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(static a => a.Policy).Single(static p => p is not null)!)))
            .ToList();
        var callers = new (string Name, string? Token)[]
        {
            ("anonymous", null),
            ("tenant user", TestIssuer.Shared.CreateToken("matrix-user")),
            ("platform token without a role", TestIssuer.Platform.CreateToken("matrix-nobody")),
            ("platform admin", TestIssuer.Platform.CreateOperatorToken("matrix-admin")),
            ("support operator", TestIssuer.Platform.CreateSupportToken("matrix-support")),
        };

        var failures = new List<string>();
        foreach (var route in routes)
        {
            foreach (var (name, token) in callers)
            {
                var path = route.Pattern
                    .Replace("{id}", Guid.CreateVersion7().ToString(), StringComparison.Ordinal)
                    .Replace("{tenantId}", Guid.CreateVersion7().ToString(), StringComparison.Ordinal)
                    .Replace("{grantId}", Guid.CreateVersion7().ToString(), StringComparison.Ordinal);
                using var response = await SendAsync(new HttpMethod(route.Method), path, token, route.Method is "POST" or "PUT" ? new { } : null);
                var allowed = (name, route.Policy) switch
                {
                    ("platform admin", AuthenticationExtensions.PlatformAdminPolicy or AuthenticationExtensions.PlatformStaffPolicy) => true,
                    ("support operator", AuthenticationExtensions.SupportOperatorPolicy or AuthenticationExtensions.PlatformStaffPolicy) => true,
                    _ => false,
                };
                var expected = allowed ? "allowed" : token is null || name == "tenant user" ? "401" : "403";
                var actual = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "401",
                    HttpStatusCode.Forbidden => "403",
                    _ => "allowed",
                };
                if (expected != actual)
                {
                    failures.Add($"{route.Method} {route.Pattern} as {name}: expected {expected}, got {(int)response.StatusCode}");
                }
            }
        }

        Assert.Equal(10, routes.Count);
        Assert.Empty(failures);
    }

    // ---- criterion 16 ----

    [Fact]
    public async Task A_support_operator_opens_one_audited_grant_with_a_ticket_and_a_bounded_duration()
    {
        var tenant = await fixture.Harness.CreateTenantAsync("Support open");
        var support = TestIssuer.Platform.CreateSupportToken($"support-{Guid.NewGuid():N}");

        using var opened = await OpenAsync(support, tenant, "SUP-1234", 60);
        using var again = await OpenAsync(support, tenant, "SUP-1235", 30);
        using var unknown = await OpenAsync(support, Guid.CreateVersion7(), "SUP-1", 30);
        using var freeText = await OpenAsync(support, tenant, "the customer called", 30);
        using var tooLong = await OpenAsync(support, tenant, "SUP-1", 241);
        using var byAdmin = await OpenAsync(TestIssuer.Platform.CreateOperatorToken("admin-open"), tenant, "SUP-1", 30);

        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        var grant = await PaymentScenario.JsonAsync(opened);
        Assert.Equal((tenant, "SUP-1234", Start.AddMinutes(60)), (grant.GetProperty("tenantId").GetGuid(), grant.GetProperty("ticket").GetString(), grant.GetProperty("expiresAt").GetDateTimeOffset()));
        Assert.Equal((HttpStatusCode.Conflict, "support.grant_exists"), (again.StatusCode, await PaymentScenario.CodeAsync(again)));
        Assert.Equal((HttpStatusCode.NotFound, "tenant.not_found"), (unknown.StatusCode, await PaymentScenario.CodeAsync(unknown)));
        Assert.Equal((HttpStatusCode.BadRequest, "support.ticket_invalid"), (freeText.StatusCode, await PaymentScenario.CodeAsync(freeText)));
        Assert.Equal((HttpStatusCode.BadRequest, "support.duration_invalid"), (tooLong.StatusCode, await PaymentScenario.CodeAsync(tooLong)));
        Assert.Equal(HttpStatusCode.Forbidden, byAdmin.StatusCode);
        // Visible to the tenant: the event names the tenant, the operator by reference, and the grant.
        Assert.Equal([("support.grant_opened", "support_operator", grant.GetProperty("id").GetGuid())], await EventsAsync(tenant));
    }

    // ---- criterion 17 ----

    [Fact]
    public async Task The_views_need_the_callers_own_active_grant_for_that_tenant_and_record_each_access_before_reading()
    {
        var tenant = await fixture.Harness.CreateTenantAsync("Support views");
        var other = await fixture.Harness.CreateTenantAsync("Support other");
        var member = await fixture.Harness.AddAsync(tenant, $"member-{Guid.NewGuid():N}", "tenant-admin", TestIssuer.Issuer);
        var support = TestIssuer.Platform.CreateSupportToken($"support-{Guid.NewGuid():N}");
        var colleague = TestIssuer.Platform.CreateSupportToken($"colleague-{Guid.NewGuid():N}");

        using var before = await SendAsync(HttpMethod.Get, $"/v1/platform/support/tenants/{tenant}/members", support);
        using var opened = await OpenAsync(support, tenant, "SUP-77", 30);
        using var members = await SendAsync(HttpMethod.Get, $"/v1/platform/support/tenants/{tenant}/members", support);
        using var jobs = await SendAsync(HttpMethod.Get, $"/v1/platform/support/tenants/{tenant}/jobs", support);
        using var byColleague = await SendAsync(HttpMethod.Get, $"/v1/platform/support/tenants/{tenant}/members", colleague);
        using var otherTenant = await SendAsync(HttpMethod.Get, $"/v1/platform/support/tenants/{other}/jobs", support);

        Assert.Equal((HttpStatusCode.NotFound, "tenant.not_found"), (before.StatusCode, await PaymentScenario.CodeAsync(before)));
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (members.StatusCode, jobs.StatusCode));
        var item = Assert.Single((await PaymentScenario.JsonAsync(members)).GetProperty("items").EnumerateArray());
        Assert.Equal(member.Id, item.GetProperty("membershipId").GetGuid());
        Assert.Equal(["createdAt", "membershipId", "role", "status", "updatedAt"], item.EnumerateObject().Select(static p => p.Name).Order());
        Assert.Empty((await PaymentScenario.JsonAsync(jobs)).GetProperty("items").EnumerateArray());
        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (byColleague.StatusCode, otherTenant.StatusCode));
        var grant = (await PaymentScenario.JsonAsync(opened)).GetProperty("id").GetGuid();
        Assert.Equal(
            new List<(string, string, Guid)> { ("support.grant_opened", "support_operator", grant), ("support.tenant_accessed", "support_operator", grant), ("support.tenant_accessed", "support_operator", grant) }.Order(),
            await EventsAsync(tenant));
        Assert.Empty(await EventsAsync(other));
    }

    // ---- criterion 18 ----

    [Fact]
    public async Task An_ended_revoked_or_expired_grant_answers_404_from_the_next_access_and_each_end_is_audited()
    {
        var tenant = await fixture.Harness.CreateTenantAsync("Support end");
        var support = TestIssuer.Platform.CreateSupportToken($"support-{Guid.NewGuid():N}");
        var colleague = TestIssuer.Platform.CreateSupportToken($"colleague-{Guid.NewGuid():N}");
        var admin = TestIssuer.Platform.CreateOperatorToken("admin-revoke");
        var view = $"/v1/platform/support/tenants/{tenant}/members";

        var first = await GrantIdAsync(await OpenAsync(support, tenant, "SUP-1", 30));
        using var byColleague = await SendAsync(HttpMethod.Delete, $"/v1/platform/support/grants/{first}", colleague);
        using var ended = await SendAsync(HttpMethod.Delete, $"/v1/platform/support/grants/{first}", support);
        using var afterEnd = await SendAsync(HttpMethod.Get, view, support);
        var second = await GrantIdAsync(await OpenAsync(support, tenant, "SUP-2", 30));
        using var revoked = await SendAsync(HttpMethod.Delete, $"/v1/platform/support/grants/{second}", admin);
        using var afterRevoke = await SendAsync(HttpMethod.Get, view, support);
        var third = await GrantIdAsync(await OpenAsync(support, tenant, "SUP-3", 1));
        _clock.Advance(TimeSpan.FromMinutes(1));
        using var afterExpiry = await SendAsync(HttpMethod.Get, view, support);
        using var endExpired = await SendAsync(HttpMethod.Delete, $"/v1/platform/support/grants/{third}", support);

        Assert.Equal(HttpStatusCode.NotFound, byColleague.StatusCode);
        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (ended.StatusCode, revoked.StatusCode));
        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound), (afterEnd.StatusCode, afterRevoke.StatusCode, afterExpiry.StatusCode));
        Assert.Equal(HttpStatusCode.NotFound, endExpired.StatusCode);
        Assert.Equal(
            new List<(string, string, Guid)>
            {
                ("support.grant_opened", "support_operator", first), ("support.grant_ended", "support_operator", first),
                ("support.grant_opened", "support_operator", second), ("support.grant_ended", "platform_admin", second),
                ("support.grant_opened", "support_operator", third),
            }.Order(),
            await EventsAsync(tenant));
    }

    // ---- helpers ----

    private Task<HttpResponseMessage> OpenAsync(string token, Guid tenant, string ticket, int minutes) =>
        SendAsync(HttpMethod.Post, "/v1/platform/support/grants", token, new { tenantId = tenant, ticket, minutes });

    private static async Task<Guid> GrantIdAsync(HttpResponseMessage opened)
    {
        using (opened)
        {
            Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
            return (await PaymentScenario.JsonAsync(opened)).GetProperty("id").GetGuid();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token, object? body = null)
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, Cancellation);
    }

    // The tenant's support audit events in the Operations outbox, sorted: (action, actor kind, grant).
    private async Task<List<(string, string, Guid)>> EventsAsync(Guid tenant)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = new NpgsqlCommand(
            "SELECT payload::text FROM operations.outbox_messages WHERE tenant_id = @tenant AND type = 'audit.recorded.v1' ORDER BY occurred_at, id",
            connection);
        command.Parameters.AddWithValue("tenant", tenant);
        await using var reader = await command.ExecuteReaderAsync(Cancellation);
        var events = new List<(string, string, Guid)>();
        while (await reader.ReadAsync(Cancellation))
        {
            using var payload = JsonDocument.Parse(reader.GetString(0));
            var root = payload.RootElement;
            events.Add((root.GetProperty("action").GetString()!, root.GetProperty("actorKind").GetString()!, root.GetProperty("targetId").GetGuid()));
        }

        // Sorted: under the fake clock several events share a time, so the database order between them is arbitrary.
        return [.. events.Order()];
    }
}
