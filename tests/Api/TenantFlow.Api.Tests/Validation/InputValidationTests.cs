using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace TenantFlow.Api.Tests.Validation;

/// <summary>
/// Input validation at the HTTP edge (pre-S5 review 2.4): ids that cannot name a tenant, characters that
/// must not be stored, how much body the API will read, and the size of identity claims. The tenant-name
/// rule itself is tested without a host in the Tenants unit tests.
/// </summary>
public sealed class InputValidationTests
{
    private const long ExpectedBodyLimit = 64 * 1024;

    [Theory]
    [InlineData("GET", "not-a-guid")]
    [InlineData("GET", "123")]
    [InlineData("GET", "{00000000-0000-0000-0000-000000000001}")]
    [InlineData("POST", "not-a-guid/suspend")]
    [InlineData("POST", "not-a-guid/reactivate")]
    public async Task A_malformed_tenant_id_answers_exactly_like_an_unknown_tenant(string method, string tail)
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = Client(factory);
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri($"/v1/platform/tenants/{tail}", UriKind.Relative));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // Not a 401 from the deny-by-default fallback, and no database was needed to decide it.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal("tenant.not_found", problem.GetProperty("code").GetString());
        Assert.Equal("The tenant does not exist.", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_malformed_tenant_id_is_still_a_401_for_an_anonymous_caller()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/v1/platform/tenants/not-a-guid", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("a\\u0000b")]
    [InlineData("line one\\nline two")]
    [InlineData("tab\\there")]
    [InlineData("spoofed \\u202Egnp.exe")]
    [InlineData("isolate \\u2066text")]
    public async Task Control_and_direction_override_characters_in_a_name_are_refused(string escapedName)
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = Client(factory);

        using var response = await client.PostAsync(
            new Uri("/v1/platform/tenants", UriKind.Relative),
            new StringContent($$"""{"name":"{{escapedName}}"}""", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        var error = Assert.Single(problem.GetProperty("errors").EnumerateArray());
        Assert.Equal("name", error.GetProperty("field").GetString());
        Assert.Equal("tenant.name.invalid_characters", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Request_bodies_are_limited_to_64_KiB()
    {
        // TestServer does not enforce Kestrel's limit, so this checks the configured value; the smoke tests (tests/Smoke)
        // sends an oversized body to the real Kestrel host and expects a 413.
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();

        var limits = factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits;

        Assert.Equal(ExpectedBodyLimit, limits.MaxRequestBodySize);
    }

    [Theory]
    [InlineData(255, HttpStatusCode.OK)]
    [InlineData(256, HttpStatusCode.Unauthorized)]
    public async Task A_token_subject_longer_than_the_audit_column_is_refused_up_front(int length, HttpStatusCode expected)
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/v1/me", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Shared.CreateToken(subject: new string('s', length)));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("Authentication:Tenant:Authority")]
    [InlineData("Authentication:Platform:Authority")]
    public async Task An_authority_longer_than_the_audit_column_stops_startup_naming_only_the_key(string key)
    {
        await using var factory = ApiFactory.CreateProduction(settings: new Dictionary<string, string>
        {
            [key] = "https://idp.example.com/" + new string('a', 500),
        });

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        var text = exception.ToString();
        Assert.Contains(key, text, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', 100), text, StringComparison.Ordinal);
    }

    private static HttpClient Client(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Platform.CreateOperatorToken());
        return client;
    }
}
