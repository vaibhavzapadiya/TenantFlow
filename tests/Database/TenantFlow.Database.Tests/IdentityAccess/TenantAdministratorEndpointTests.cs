using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TenantFlow.Database.Tests.IdentityAccess;

/// <summary>
/// SPEC-0002 criterion 24 (admins part) over HTTP and real PostgreSQL: a platform operator creates the first
/// administrator of a tenant. The tenant must exist and be active; the call is audited with the operator as actor; the
/// new administrator then sees the membership through their own token. Tenant users never reach this route.
/// </summary>
public sealed class TenantAdministratorEndpointTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;

    private WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("Not initialized.");

    public ValueTask InitializeAsync()
    {
        _factory = ApiFactory.CreateProduction(connectionString: fixture.Database.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task Operator_creates_an_administrator_the_membership_is_audited_and_the_user_can_see_it()
    {
        var tenant = await fixture.Harness.CreateTenantAsync();
        var subject = $"first-admin-{Guid.NewGuid():N}";

        using var response = await PostAsync(tenant.ToString(), new { subject }, TestIssuer.Platform.CreateOperatorToken("operator-a"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(tenant, body.RootElement.GetProperty("tenantId").GetGuid());
        Assert.Equal(TestIssuer.Issuer, body.RootElement.GetProperty("issuer").GetString());
        Assert.Equal(subject, body.RootElement.GetProperty("subject").GetString());
        Assert.Equal("tenant-admin", body.RootElement.GetProperty("role").GetString());
        var membershipId = body.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(
            [$"membership.added|{TestIssuer.PlatformIssuer}|operator-a|{membershipId}||tenant-admin"],
            await MigratorRun.QueryAsync<string>(
                fixture.Database.OwnerConnectionString,
                "SELECT action || '|' || actor_issuer || '|' || actor_subject || '|' || membership_id || '|' || coalesce(old_role, '') || '|' || coalesce(new_role, '') FROM identity_access.membership_audit WHERE tenant_id = @t",
                ("t", tenant)));
        using var mine = await GetMyMembershipsAsync(TestIssuer.Shared.CreateToken(subject));
        using var memberships = JsonDocument.Parse(await mine.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var item = Assert.Single(memberships.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(membershipId, item.GetProperty("membershipId").GetGuid());
        Assert.Equal("tenant-admin", item.GetProperty("role").GetString());
    }

    [Fact]
    public async Task A_second_call_for_the_same_user_is_409_and_writes_nothing()
    {
        var tenant = await fixture.Harness.CreateTenantAsync();
        var subject = $"admin-{Guid.NewGuid():N}";
        using var first = await PostAsync(tenant.ToString(), new { subject }, TestIssuer.Platform.CreateOperatorToken());

        using var second = await PostAsync(tenant.ToString(), new { subject }, TestIssuer.Platform.CreateOperatorToken());

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("membership.already_exists", await ReadCodeAsync(second));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM identity_access.membership_audit WHERE tenant_id = @t", ("t", tenant)));
    }

    [Fact]
    public async Task Unknown_or_malformed_tenant_ids_are_404_tenant_not_found_and_write_nothing()
    {
        var before = await fixture.CountAsync("SELECT count(*) FROM identity_access.memberships");

        using var unknown = await PostAsync(Guid.CreateVersion7().ToString(), new { subject = "x" }, TestIssuer.Platform.CreateOperatorToken());
        using var malformed = await PostAsync("not-a-guid", new { subject = "x" }, TestIssuer.Platform.CreateOperatorToken());
        using var empty = await PostAsync(Guid.Empty.ToString(), new { subject = "x" }, TestIssuer.Platform.CreateOperatorToken());

        Assert.All([unknown, malformed, empty], response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        Assert.Equal("tenant.not_found", await ReadCodeAsync(unknown));
        Assert.Equal("tenant.not_found", await ReadCodeAsync(malformed));
        Assert.Equal("tenant.not_found", await ReadCodeAsync(empty));
        Assert.Equal(before, await fixture.CountAsync("SELECT count(*) FROM identity_access.memberships"));
    }

    [Fact]
    public async Task A_suspended_tenant_refuses_the_administrator_until_it_is_reactivated()
    {
        var tenant = await fixture.Harness.CreateTenantAsync();
        await fixture.Harness.SuspendTenantAsync(tenant);

        using var refused = await PostAsync(tenant.ToString(), new { subject = "admin-1" }, TestIssuer.Platform.CreateOperatorToken());

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("tenant.suspended", await ReadCodeAsync(refused));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM identity_access.memberships WHERE tenant_id = @t", ("t", tenant)));
    }

    [Theory]
    [InlineData("""{}""", "membership.subject.required")]
    [InlineData("""{"subject":""}""", "membership.subject.required")]
    [InlineData("""{"subject":"   "}""", "membership.subject.required")]
    [InlineData("""{"subject":"a\nb"}""", "membership.subject.invalid_characters")]
    public async Task An_invalid_subject_is_400_on_the_subject_field_and_writes_nothing(string body, string expectedCode)
    {
        var tenant = await fixture.Harness.CreateTenantAsync();

        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/platform/tenants/{tenant}/admins")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Platform.CreateOperatorToken());
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("request.validation_failed", problem.RootElement.GetProperty("code").GetString());
        var error = Assert.Single(problem.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal("subject", error.GetProperty("field").GetString());
        Assert.Equal(expectedCode, error.GetProperty("code").GetString());
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM identity_access.memberships WHERE tenant_id = @t", ("t", tenant)));
    }

    [Fact]
    public async Task Only_platform_operators_reach_the_route()
    {
        var tenant = await fixture.Harness.CreateTenantAsync();

        using var tenantUser = await PostAsync(tenant.ToString(), new { subject = "x" }, TestIssuer.Shared.CreateToken("admin-of-this-tenant"));
        using var noRole = await PostAsync(tenant.ToString(), new { subject = "x" }, TestIssuer.Platform.CreateToken("not-an-operator"));
        using var anonymous = await PostAsync(tenant.ToString(), new { subject = "x" }, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, tenantUser.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, noRole.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM identity_access.memberships WHERE tenant_id = @t", ("t", tenant)));
    }

    [Fact]
    public async Task An_administrator_of_one_tenant_does_not_appear_in_another()
    {
        var a = await fixture.Harness.CreateTenantAsync("A");
        var b = await fixture.Harness.CreateTenantAsync("B");
        var subject = $"admin-{Guid.NewGuid():N}";
        using var created = await PostAsync(a.ToString(), new { subject }, TestIssuer.Platform.CreateOperatorToken());

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM identity_access.memberships WHERE tenant_id = @t", ("t", a)));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM identity_access.memberships WHERE tenant_id = @t", ("t", b)));
    }

    private async Task<HttpResponseMessage> PostAsync(string tenantId, object body, string? token)
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/platform/tenants/{tenantId}/admins")
        {
            Content = JsonContent.Create(body),
        };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> GetMyMembershipsAsync(string token)
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/me/memberships");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return problem.RootElement.GetProperty("code").GetString();
    }
}
