using System.Net;
using System.Net.Http.Headers;

namespace TenantFlow.Api.Tests.Platform;

/// <summary>
/// SPEC-0002 criterion 9 (ADR-0010): tenant and platform tokens are never accepted across the
/// boundary, and platform routes require the operator role. No database is needed: every request
/// here is rejected before a handler runs.
/// </summary>
public sealed class PlatformBoundaryTests
{
    private static readonly string AnyTenantPath = $"/v1/platform/tenants/{Guid.NewGuid()}";

    [Fact]
    public async Task Tenant_token_is_rejected_on_platform_routes()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();
        var tenantToken = TestIssuer.Shared.CreateToken(roles: ["platform-admin"]);

        using var create = await SendAsync(client, HttpMethod.Post, "/v1/platform/tenants", tenantToken);
        using var read = await SendAsync(client, HttpMethod.Get, AnyTenantPath, tenantToken);

        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
    }

    [Fact]
    public async Task Tenant_issuer_token_with_the_platform_audience_is_still_rejected()
    {
        // The separate issuer is the boundary: a misconfigured audience mapper cannot cross it.
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();
        var token = TestIssuer.Shared.CreateToken(audience: TestIssuer.PlatformAudience, roles: ["platform-admin"]);

        using var response = await SendAsync(client, HttpMethod.Get, AnyTenantPath, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Platform_token_is_rejected_on_tenant_routes()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Get, "/v1/me", TestIssuer.Platform.CreateOperatorToken());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Platform_token_without_the_operator_role_is_forbidden()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();

        using var noRole = await SendAsync(client, HttpMethod.Get, AnyTenantPath, TestIssuer.Platform.CreateToken("operator-b"));
        using var otherRole = await SendAsync(client, HttpMethod.Get, AnyTenantPath, TestIssuer.Platform.CreateToken("operator-c", roles: ["support"]));

        Assert.Equal(HttpStatusCode.Forbidden, noRole.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, otherRole.StatusCode);
    }

    [Fact]
    public async Task Anonymous_caller_is_challenged_on_platform_routes()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Post, "/v1/platform/tenants", token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("Authentication:Platform:Authority")]
    [InlineData("Authentication:Platform:Audience")]
    public async Task Host_refuses_to_start_without_a_platform_authentication_setting(string key)
    {
        await using var factory = ApiFactory.CreateProduction(settings: new Dictionary<string, string> { [key] = string.Empty });

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        Assert.Contains(key, exception.ToString(), StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? token)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (method == HttpMethod.Post)
        {
            request.Content = new StringContent("""{"name":"Acme"}""", System.Text.Encoding.UTF8, "application/json");
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
