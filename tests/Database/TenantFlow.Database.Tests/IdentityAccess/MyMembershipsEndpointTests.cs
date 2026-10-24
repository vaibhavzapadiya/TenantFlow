using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TenantFlow.Database.Tests.IdentityAccess;

/// <summary>
/// SPEC-0002 criterion 16 over HTTP: <c>GET /v1/me/memberships</c> returns only the caller's own active memberships in
/// active tenants, matched on issuer plus subject. It takes no tenant id, so there is nothing to forge.
/// </summary>
public sealed class MyMembershipsEndpointTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
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
    public async Task A_member_sees_only_their_own_memberships_and_nobody_elses()
    {
        var harness = fixture.Harness;
        var a = await harness.CreateTenantAsync("A");
        var b = await harness.CreateTenantAsync("B");
        var u = $"user-u-{Guid.NewGuid():N}";
        var v = $"user-v-{Guid.NewGuid():N}";
        var uInA = await harness.AddAsync(a, u, "customer", TestIssuer.Issuer);
        await harness.AddAsync(b, v, "tenant-admin", TestIssuer.Issuer);

        using var response = await GetAsync(TestIssuer.Shared.CreateToken(u));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await ReadItemsAsync(response);
        var item = Assert.Single(items);
        Assert.Equal(uInA.Id, item.GetProperty("membershipId").GetGuid());
        Assert.Equal(a, item.GetProperty("tenantId").GetGuid());
        Assert.Equal("customer", item.GetProperty("role").GetString());
    }

    [Fact]
    public async Task The_same_subject_from_a_different_issuer_is_not_matched()
    {
        var harness = fixture.Harness;
        var tenant = await harness.CreateTenantAsync();
        var subject = $"user-{Guid.NewGuid():N}";
        await harness.AddAsync(tenant, subject, "tenant-admin", TenantHarness.OtherIssuer);

        using var response = await GetAsync(TestIssuer.Shared.CreateToken(subject));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await ReadItemsAsync(response));
    }

    [Fact]
    public async Task Suspended_tenants_and_removed_memberships_are_not_listed_and_a_caller_with_none_gets_an_empty_list()
    {
        var harness = fixture.Harness;
        var suspended = await harness.CreateTenantAsync("S");
        var left = await harness.CreateTenantAsync("L");
        var subject = $"user-{Guid.NewGuid():N}";
        await harness.AddAsync(suspended, subject, "customer", TestIssuer.Issuer);
        await harness.AddAsync(left, "boss", "tenant-admin", TestIssuer.Issuer);
        var gone = await harness.AddAsync(left, subject, "customer", TestIssuer.Issuer);
        await harness.RemoveAsync(left, gone.Id);
        await harness.SuspendTenantAsync(suspended);

        using var response = await GetAsync(TestIssuer.Shared.CreateToken(subject));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await ReadItemsAsync(response));
    }

    [Fact]
    public async Task Without_a_token_or_with_a_platform_token_the_route_answers_401()
    {
        using var anonymous = await GetAsync(token: null);
        using var platform = await GetAsync(TestIssuer.Platform.CreateOperatorToken("operator-a"));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, platform.StatusCode);
        Assert.Equal("application/problem+json", anonymous.Content.Headers.ContentType?.MediaType);
    }

    private async Task<HttpResponseMessage> GetAsync(string? token)
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/me/memberships");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<List<JsonElement>> ReadItemsAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return [.. document.RootElement.GetProperty("items").EnumerateArray().Select(static item => item.Clone())];
    }
}
