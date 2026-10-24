using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TenantFlow.Api.Extensions;

namespace TenantFlow.Api.Tests.Contract;

/// <summary>
/// The interactive API reference (Scalar) over the v1 OpenAPI document: served anonymously in Development only, with a
/// relaxed Content-Security-Policy on its own endpoints and the baseline policy (ADR-0017) on every other response. A
/// Production host never maps it, even when the document itself is switched on.
/// </summary>
public sealed partial class ApiReferenceTests
{
    private const string BaselinePolicy = "default-src 'none'; frame-ancestors 'none'";

    [Fact]
    public async Task In_development_the_reference_page_is_served_anonymously_from_the_v1_document()
    {
        await using var factory = ApiFactory.CreateDevelopment();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/scalar/v1", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<title>TenantFlow API</title>", body, StringComparison.Ordinal);
        Assert.Contains("\"url\":\"openapi/v1.json\"", body, StringComparison.Ordinal);
        // The script is served by the package itself, not a CDN, and the document it reads is the one already published.
        using var script = await client.GetAsync(new Uri("/scalar/scalar.js", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        using var document = await client.GetAsync(new Uri(OpenApiExtensions.DocumentPath, UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
    }

    [Fact]
    public async Task In_development_only_the_reference_endpoints_carry_the_relaxed_content_security_policy()
    {
        await using var factory = ApiFactory.CreateDevelopment();
        using var client = factory.CreateClient();

        var reference = OpenApiExtensions.ReferenceContentSecurityPolicyFor(["https://issuer.tenantflow.test"]);
        Assert.Equal(reference, await PolicyOfAsync(client, "/scalar/v1"));
        Assert.Equal(reference, await PolicyOfAsync(client, "/scalar/scalar.js"));
        Assert.Equal(BaselinePolicy, await PolicyOfAsync(client, OpenApiExtensions.DocumentPath));
        Assert.Equal(BaselinePolicy, await PolicyOfAsync(client, "/v1/me"));
        Assert.Equal(BaselinePolicy, await PolicyOfAsync(client, "/scalar/v1/no-such-asset"));
    }

    [Fact]
    public async Task A_production_host_does_not_map_the_reference_even_when_the_document_is_enabled()
    {
        await using var factory = ApiFactory.CreateProduction(settings: new Dictionary<string, string> { [OpenApiExtensions.EnabledKey] = "true" });
        using var client = factory.CreateClient();

        var patterns = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(static route => route.RoutePattern.RawText!)
            .ToList();
        using var response = await client.GetAsync(new Uri("/scalar/v1", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Contains(OpenApiExtensions.DocumentPath, patterns);
        Assert.DoesNotContain(patterns, static pattern => pattern.StartsWith(OpenApiExtensions.ReferencePathPrefix, StringComparison.Ordinal));
        // No endpoint matches, so the fallback policy denies the anonymous caller like any unknown path.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(BaselinePolicy, Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
    }

    // SPEC-0014 criterion 1: two sign-in schemes, authorization code with PKCE, URLs built from the configured issuers. The
    // scheme and its public client id are in the Development document (Scalar pre-fills the client only from there); PKCE
    // and the preferred scheme are the page's own settings.
    [Fact]
    public async Task In_development_the_reference_signs_in_tenant_users_and_platform_operators_with_pkce()
    {
        await using var factory = ApiFactory.CreateDevelopment();
        using var client = factory.CreateClient();

        var configuration = await ConfigurationOfAsync(client);
        var document = JsonNode.Parse(await client.GetStringAsync(new Uri(OpenApiExtensions.DocumentPath, UriKind.Relative), TestContext.Current.CancellationToken))!;
        var schemes = document["components"]?["securitySchemes"];
        var pages = configuration["authentication"]?["securitySchemes"];

        Assert.Equal(OpenApiExtensions.TenantSignInScheme, (string?)configuration["authentication"]?["preferredSecurityScheme"]?[0]);
        AssertSignIn(schemes?[OpenApiExtensions.TenantSignInScheme], pages?[OpenApiExtensions.TenantSignInScheme], TestIssuer.Issuer, "tenantflow-api-docs");
        AssertSignIn(schemes?[OpenApiExtensions.PlatformSignInScheme], pages?[OpenApiExtensions.PlatformSignInScheme], TestIssuer.PlatformIssuer, "tenantflow-platform-api-docs");
        // SPEC-0014 criterion 8: the token stays in the page's memory.
        Assert.NotEqual(true, (bool?)configuration["persistAuth"]);
    }

    // SPEC-0014 criterion 2: the reference may call exactly the configured issuers' origins, nothing else.
    [Fact]
    public async Task The_reference_policy_allows_connections_to_exactly_the_issuer_origins()
    {
        await using var factory = ApiFactory.CreateDevelopment(settings: new Dictionary<string, string>
        {
            ["Authentication:Tenant:Authority"] = "https://tenant-issuer.tenantflow.test/realms/tenantflow",
            ["Authentication:Platform:Authority"] = "https://platform-issuer.tenantflow.test:8443/realms/tenantflow-platform",
        });
        using var client = factory.CreateClient();

        var policy = await PolicyOfAsync(client, "/scalar/v1");
        var connect = policy.Split(';', StringSplitOptions.TrimEntries).Single(static directive => directive.StartsWith("connect-src ", StringComparison.Ordinal));

        Assert.Equal("connect-src 'self' https://tenant-issuer.tenantflow.test https://platform-issuer.tenantflow.test:8443", connect);
        Assert.Equal(BaselinePolicy, await PolicyOfAsync(client, OpenApiExtensions.DocumentPath));
    }

    // SPEC-0014 criterion 3: the sign-in lives in the Development page only, never in the published contract.
    [Fact]
    public async Task The_published_document_carries_no_sign_in_configuration()
    {
        await using var factory = ApiFactory.CreateProduction(settings: new Dictionary<string, string> { [OpenApiExtensions.EnabledKey] = "true" });
        using var client = factory.CreateClient();

        var document = await client.GetStringAsync(new Uri(OpenApiExtensions.DocumentPath, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("securitySchemes", document, StringComparison.Ordinal);
        Assert.DoesNotContain("-api-docs", document, StringComparison.Ordinal);
        Assert.DoesNotContain("openid-connect", document, StringComparison.Ordinal);
    }

    private static void AssertSignIn(JsonNode? scheme, JsonNode? page, string authority, string clientId)
    {
        Assert.NotNull(scheme);
        Assert.Equal("oauth2", (string?)scheme["type"]);
        var flow = scheme["flows"]?["authorizationCode"];
        Assert.NotNull(flow);
        Assert.Equal($"{authority}/protocol/openid-connect/auth", (string?)flow["authorizationUrl"]);
        Assert.Equal($"{authority}/protocol/openid-connect/token", (string?)flow["tokenUrl"]);
        Assert.Equal(clientId, (string?)flow["x-scalar-client-id"]);
        Assert.Equal("SHA-256", (string?)page?["flows"]?["authorizationCode"]?["x-usePkce"]);
    }

    // The page embeds its configuration as one JSON object; the test reads it the way the page's script does.
    private static async Task<JsonNode> ConfigurationOfAsync(HttpClient client)
    {
        var body = await client.GetStringAsync(new Uri("/scalar/v1", UriKind.Relative), TestContext.Current.CancellationToken);
        var match = ConfigurationScript().Match(body);
        Assert.True(match.Success, "The reference page embeds no configuration.");
        return JsonNode.Parse(match.Groups["json"].Value)!;
    }

    private static async Task<string> PolicyOfAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        return Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
    }

    // The third argument of the page's initialize(...) call: one line holding the configuration object.
    [GeneratedRegex(@"(?m)^\s*(?<json>\{.*\}),\s*$")]
    private static partial Regex ConfigurationScript();
}
