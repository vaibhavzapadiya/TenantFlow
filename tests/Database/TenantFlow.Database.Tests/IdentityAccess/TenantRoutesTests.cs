using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TenantFlow.Database.Tests.IdentityAccess;

/// <summary>
/// SPEC-0002 criteria 12 to 14, 17 and 18, plus the HTTP side of 19 to 21, against real PostgreSQL. Two tenants, A and B,
/// each with an administrator, and a customer in A: every request is made with a real tenant-scheme token, so what is
/// proven is what a caller on the wire gets.
/// </summary>
public sealed class TenantRoutesTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
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

    private sealed record World(Guid TenantA, Guid TenantB, string AdminA, string CustomerA, string AdminB, Guid CustomerAMembership, Guid AdminBMembership, Guid AdminAMembership);

    private async Task<World> CreateWorldAsync()
    {
        var harness = fixture.Harness;
        var suffix = Guid.NewGuid().ToString("N");
        var a = await harness.CreateTenantAsync("Tenant A");
        var b = await harness.CreateTenantAsync("Tenant B");
        var adminA = $"admin-a-{suffix}";
        var customerA = $"customer-a-{suffix}";
        var adminB = $"admin-b-{suffix}";
        var adminAMembership = await harness.AddAsync(a, adminA, "tenant-admin", TestIssuer.Issuer);
        var customerAMembership = await harness.AddAsync(a, customerA, "customer", TestIssuer.Issuer);
        var adminBMembership = await harness.AddAsync(b, adminB, "tenant-admin", TestIssuer.Issuer);
        return new World(a, b, adminA, customerA, adminB, customerAMembership.Id, adminBMembership.Id, adminAMembership.Id);
    }

    // ---- criterion 12 and 13: a non-member learns nothing and changes nothing ----

    [Fact]
    public async Task A_non_member_reading_another_tenant_gets_the_same_404_as_an_unknown_or_malformed_id()
    {
        var w = await CreateWorldAsync();

        using var other = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantB}", w.AdminA);
        using var unknown = await SendAsync(HttpMethod.Get, $"/v1/tenants/{Guid.CreateVersion7()}", w.AdminA);
        using var malformed = await SendAsync(HttpMethod.Get, "/v1/tenants/not-a-guid", w.AdminA);
        using var empty = await SendAsync(HttpMethod.Get, $"/v1/tenants/{Guid.Empty}", w.AdminA);

        Assert.All([other, unknown, malformed, empty], response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        var expected = await ShapeAsync(other);
        Assert.StartsWith("404|tenant.not_found|", expected, StringComparison.Ordinal);
        Assert.Equal(expected, await ShapeAsync(unknown));
        Assert.Equal(expected, await ShapeAsync(malformed));
        Assert.Equal(expected, await ShapeAsync(empty));
    }

    [Fact]
    public async Task Every_route_of_another_tenant_is_404_and_changes_and_audits_nothing()
    {
        var w = await CreateWorldAsync();
        var membersBefore = await RowsAsync(w.TenantB);
        var auditBefore = await AuditCountAsync(w.TenantB);
        var someMember = Guid.CreateVersion7();

        using var list = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantB}/members", w.AdminA);
        using var add = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantB}/members", w.AdminA, new { subject = "intruder", role = "tenant-admin" });
        using var role = await SendAsync(HttpMethod.Put, $"/v1/tenants/{w.TenantB}/members/{someMember}/role", w.AdminA, new { role = "customer" });
        using var remove = await SendAsync(HttpMethod.Delete, $"/v1/tenants/{w.TenantB}/members/{w.AdminBMembership}", w.AdminA);

        Assert.All([list, add, role, remove], response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        await AssertCodeAsync("tenant.not_found", list, add, role, remove);
        Assert.Equal(membersBefore, await RowsAsync(w.TenantB));
        Assert.Equal(auditBefore, await AuditCountAsync(w.TenantB));
    }

    [Fact]
    public async Task A_valid_body_does_not_make_a_non_member_a_member()
    {
        var w = await CreateWorldAsync();

        using var response = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantB}/members", w.AdminA, new { subject = w.AdminA, role = "tenant-admin" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await fixture.CountAsync(
            "SELECT count(*) FROM identity_access.memberships WHERE tenant_id = @t AND subject = @s", ("t", w.TenantB), ("s", w.AdminA)));
    }

    // ---- criterion 14: removal and suspension ----

    [Fact]
    public async Task A_removed_member_is_refused_with_the_same_token_on_the_next_call()
    {
        var w = await CreateWorldAsync();
        using var before = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}", w.CustomerA);
        await fixture.Harness.RemoveAsync(w.TenantA, w.CustomerAMembership);

        using var after = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}", w.CustomerA);

        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
        Assert.Equal("tenant.not_found", await ReadCodeAsync(after));
    }

    [Fact]
    public async Task A_suspended_tenant_is_403_to_its_members_and_404_to_everyone_else()
    {
        var w = await CreateWorldAsync();
        await fixture.Harness.SuspendTenantAsync(w.TenantA);

        using var admin = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}", w.AdminA);
        using var customer = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}/members", w.CustomerA);
        using var otherTenantsAdmin = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}", w.AdminB);

        Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);
        Assert.Equal("membership.tenant_suspended", await ReadCodeAsync(admin));
        Assert.Equal(HttpStatusCode.Forbidden, customer.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherTenantsAdmin.StatusCode);
        Assert.Equal("tenant.not_found", await ReadCodeAsync(otherTenantsAdmin));
    }

    // ---- criterion 18: customers cannot manage members ----

    [Fact]
    public async Task A_customer_can_read_the_tenant_but_cannot_list_or_manage_members()
    {
        var w = await CreateWorldAsync();
        var membersBefore = await RowsAsync(w.TenantA);
        var auditBefore = await AuditCountAsync(w.TenantA);

        using var tenant = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}", w.CustomerA);
        using var list = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}/members", w.CustomerA);
        using var add = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/members", w.CustomerA, new { subject = "friend", role = "customer" });
        using var promoteSelf = await SendAsync(HttpMethod.Put, $"/v1/tenants/{w.TenantA}/members/{w.CustomerAMembership}/role", w.CustomerA, new { role = "tenant-admin" });
        using var demoteAdmin = await SendAsync(HttpMethod.Put, $"/v1/tenants/{w.TenantA}/members/{w.AdminAMembership}/role", w.CustomerA, new { role = "customer" });
        using var removeSelf = await SendAsync(HttpMethod.Delete, $"/v1/tenants/{w.TenantA}/members/{w.CustomerAMembership}", w.CustomerA);

        Assert.Equal(HttpStatusCode.OK, tenant.StatusCode);
        Assert.All([list, add, promoteSelf, demoteAdmin, removeSelf], response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
        await AssertCodeAsync("membership.permission_denied", list, add, promoteSelf, demoteAdmin, removeSelf);
        Assert.Equal(membersBefore, await RowsAsync(w.TenantA));
        Assert.Equal(auditBefore, await AuditCountAsync(w.TenantA));
    }

    // ---- criterion 17: ids from another tenant are not found ----

    [Fact]
    public async Task An_administrator_cannot_change_or_remove_another_tenants_membership_through_their_own_tenants_route()
    {
        var w = await CreateWorldAsync();
        var beforeB = await RowsAsync(w.TenantB);
        var auditBefore = await AuditCountAsync(w.TenantB);

        using var role = await SendAsync(HttpMethod.Put, $"/v1/tenants/{w.TenantA}/members/{w.AdminBMembership}/role", w.AdminA, new { role = "customer" });
        using var remove = await SendAsync(HttpMethod.Delete, $"/v1/tenants/{w.TenantA}/members/{w.AdminBMembership}", w.AdminA);
        using var malformed = await SendAsync(HttpMethod.Delete, $"/v1/tenants/{w.TenantA}/members/not-a-guid", w.AdminA);

        Assert.All([role, remove, malformed], response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        await AssertCodeAsync("membership.not_found", role, remove, malformed);
        Assert.Equal(beforeB, await RowsAsync(w.TenantB));
        Assert.Equal(auditBefore, await AuditCountAsync(w.TenantB));
    }

    // ---- criteria 19 to 21 over HTTP ----

    [Fact]
    public async Task An_administrator_lists_adds_changes_and_removes_members_and_each_change_is_audited_with_them_as_actor()
    {
        var w = await CreateWorldAsync();
        var newcomer = $"newcomer-{Guid.NewGuid():N}";

        using var added = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/members", w.AdminA, new { subject = newcomer, role = "customer" });
        var membershipId = JsonDocument.Parse(await added.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("id").GetGuid();
        using var duplicate = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/members", w.AdminA, new { subject = newcomer, role = "customer" });
        using var promoted = await SendAsync(HttpMethod.Put, $"/v1/tenants/{w.TenantA}/members/{membershipId}/role", w.AdminA, new { role = "tenant-admin" });
        using var listed = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}/members", w.AdminA);
        using var removed = await SendAsync(HttpMethod.Delete, $"/v1/tenants/{w.TenantA}/members/{membershipId}", w.AdminA);
        using var listedAfter = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}/members", w.AdminA);

        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("membership.already_exists", await ReadCodeAsync(duplicate));
        Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        Assert.Equal(["tenant-admin"], await RolesOfAsync(membershipId));
        Assert.Contains(newcomer, await SubjectsAsync(listed));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.DoesNotContain(newcomer, await SubjectsAsync(listedAfter));
        var rows = await MigratorRun.QueryAsync<string>(
            fixture.Database.OwnerConnectionString,
            "SELECT action || '|' || actor_subject || '|' || coalesce(old_role, '') || '|' || coalesce(new_role, '') FROM identity_access.membership_audit WHERE membership_id = @m ORDER BY id",
            ("m", membershipId));
        Assert.Equal(
            [$"membership.added|{w.AdminA}||customer", $"membership.role_changed|{w.AdminA}|customer|tenant-admin", $"membership.removed|{w.AdminA}|tenant-admin|"],
            rows);
    }

    [Fact]
    public async Task The_only_administrator_cannot_be_demoted_or_removed_over_http()
    {
        var w = await CreateWorldAsync();

        using var demote = await SendAsync(HttpMethod.Put, $"/v1/tenants/{w.TenantA}/members/{w.AdminAMembership}/role", w.AdminA, new { role = "customer" });
        using var remove = await SendAsync(HttpMethod.Delete, $"/v1/tenants/{w.TenantA}/members/{w.AdminAMembership}", w.AdminA);

        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
        Assert.Equal("membership.last_administrator", await ReadCodeAsync(demote));
        Assert.Equal(HttpStatusCode.Conflict, remove.StatusCode);
        Assert.Equal(["tenant-admin"], await RolesOfAsync(w.AdminAMembership));
    }

    [Theory]
    [InlineData("""{"subject":"","role":"customer"}""", "subject", "membership.subject.required")]
    [InlineData("""{"subject":"someone","role":"owner"}""", "role", "membership.role.invalid")]
    [InlineData("""{"subject":"someone"}""", "role", "membership.role.invalid")]
    public async Task Invalid_input_from_an_administrator_is_400_on_the_field_and_writes_nothing(string body, string field, string code)
    {
        var w = await CreateWorldAsync();
        var before = await RowsAsync(w.TenantA);

        using var response = await SendRawAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/members", w.AdminA, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var error = Assert.Single(problem.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal(field, error.GetProperty("field").GetString());
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal(before, await RowsAsync(w.TenantA));
    }

    // ---- boundaries ----

    [Fact]
    public async Task Without_a_token_or_with_a_platform_token_every_tenant_route_is_401()
    {
        var w = await CreateWorldAsync();
        var platform = TestIssuer.Platform.CreateOperatorToken("operator-a");
        (HttpMethod Method, string Path)[] routes =
        [
            (HttpMethod.Get, $"/v1/tenants/{w.TenantA}"),
            (HttpMethod.Get, $"/v1/tenants/{w.TenantA}/members"),
            (HttpMethod.Post, $"/v1/tenants/{w.TenantA}/members"),
            (HttpMethod.Put, $"/v1/tenants/{w.TenantA}/members/{w.AdminAMembership}/role"),
            (HttpMethod.Delete, $"/v1/tenants/{w.TenantA}/members/{w.AdminAMembership}"),
        ];

        foreach (var (method, path) in routes)
        {
            using var anonymous = await SendWithTokenAsync(method, path, token: null);
            using var withPlatform = await SendWithTokenAsync(method, path, platform);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, withPlatform.StatusCode);
        }
    }

    [Fact]
    public async Task The_members_of_a_tenant_see_the_tenant_name_and_id_only()
    {
        var w = await CreateWorldAsync();

        using var response = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}", w.CustomerA);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["id", "name"], body.RootElement.EnumerateObject().Select(static p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.StartsWith("Tenant A ", body.RootElement.GetProperty("name").GetString(), StringComparison.Ordinal);
    }

    // ---- helpers ----

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string subject, object? body = null) =>
        SendCoreAsync(method, path, TestIssuer.Shared.CreateToken(subject), body is null ? null : JsonContent.Create(body));

    private Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, string subject, string json) =>
        SendCoreAsync(method, path, TestIssuer.Shared.CreateToken(subject), new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

    private Task<HttpResponseMessage> SendWithTokenAsync(HttpMethod method, string path, string? token) =>
        SendCoreAsync(method, path, token, method == HttpMethod.Get || method == HttpMethod.Delete ? null : JsonContent.Create(new { }));

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string path, string? token, HttpContent? content)
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return problem.RootElement.GetProperty("code").GetString()!;
    }

    private static async Task AssertCodeAsync(string expected, params HttpResponseMessage[] responses)
    {
        foreach (var response in responses)
        {
            Assert.Equal(expected, await ReadCodeAsync(response));
        }
    }

    // Everything a client may compare: status, code, title, type. The trace id differs per request by design.
    private static async Task<string> ShapeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = problem.RootElement;
        return $"{(int)response.StatusCode}|{root.GetProperty("code").GetString()}|{root.GetProperty("title").GetString()}|{root.GetProperty("type").GetString()}|{response.Content.Headers.ContentType?.MediaType}";
    }

    private static async Task<List<string>> SubjectsAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return [.. body.RootElement.GetProperty("items").EnumerateArray().Select(static i => i.GetProperty("subject").GetString()!)];
    }

    private Task<IReadOnlyList<string>> RowsAsync(Guid tenant) =>
        MigratorRun.QueryAsync<string>(
            fixture.Database.OwnerConnectionString,
            "SELECT id || '|' || subject || '|' || role || '|' || status || '|' || updated_at FROM identity_access.memberships WHERE tenant_id = @t ORDER BY subject",
            ("t", tenant));

    private async Task<long> AuditCountAsync(Guid tenant) =>
        await fixture.CountAsync("SELECT count(*) FROM identity_access.membership_audit WHERE tenant_id = @t", ("t", tenant));

    private Task<IReadOnlyList<string>> RolesOfAsync(Guid membershipId) =>
        MigratorRun.QueryAsync<string>(fixture.Database.OwnerConnectionString, "SELECT role FROM identity_access.memberships WHERE id = @m", ("m", membershipId));
}
