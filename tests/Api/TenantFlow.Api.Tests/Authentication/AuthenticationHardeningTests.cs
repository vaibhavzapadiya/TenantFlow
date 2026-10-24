using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using TenantFlow.Api.Extensions;

namespace TenantFlow.Api.Tests.Authentication;

/// <summary>
/// Pre-S5 review 2.6: controls that keep authentication safe when configuration is wrong or the identity
/// provider misbehaves. Each test names the failure it prevents.
/// </summary>
public sealed class AuthenticationHardeningTests
{
    [Theory]
    [InlineData("Authentication:Platform:Authority", TestIssuer.Issuer)]
    [InlineData("Authentication:Platform:Audience", TestIssuer.Audience)]
    public async Task Tenant_and_platform_schemes_may_not_share_an_issuer_or_an_audience(string key, string tenantValue)
    {
        // Copying the tenant block into the platform block would silently collapse the ADR-0010 boundary.
        await using var factory = ApiFactory.CreateProduction(settings: new Dictionary<string, string> { [key] = tenantValue });

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        var text = exception.ToString();
        Assert.Contains(key, text, StringComparison.Ordinal);
        Assert.Contains("must differ", text, StringComparison.Ordinal);
        Assert.DoesNotContain(tenantValue, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sharing_is_detected_even_when_the_values_differ_only_in_case_or_a_trailing_slash()
    {
        await using var factory = ApiFactory.CreateProduction(settings: new Dictionary<string, string>
        {
            ["Authentication:Platform:Authority"] = TestIssuer.Issuer.ToUpperInvariant() + "/",
        });

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        Assert.Contains("Authentication:Platform:Authority", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_scheme_authenticates_by_default_so_a_bearer_token_is_only_read_where_a_policy_asks_for_it()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();
        var schemes = factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.Null(await schemes.GetDefaultAuthenticateSchemeAsync());
        // Challenge and forbid only decide how a refusal is written; they never read a token.
        Assert.Equal(AuthenticationExtensions.TenantScheme, (await schemes.GetDefaultChallengeSchemeAsync())?.Name);
        Assert.Equal(AuthenticationExtensions.TenantScheme, (await schemes.GetDefaultForbidSchemeAsync())?.Name);
    }

    [Fact]
    public async Task Liveness_does_not_wait_for_the_identity_provider_even_when_a_bearer_token_is_present()
    {
        // SPEC-0001: liveness never depends on an external system. An identity provider that accepts
        // connections and never answers must not slow /health/live down, whatever the caller sends.
        using var blackHole = new TcpListener(IPAddress.Loopback, 0);
        blackHole.Start();
        var held = new List<TcpClient>();
        using var stop = new CancellationTokenSource();
        var accepting = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    held.Add(await blackHole.AcceptTcpClientAsync(stop.Token));
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, TestContext.Current.CancellationToken);
        var port = ((IPEndPoint)blackHole.LocalEndpoint).Port;

        await using var factory = ApiFactory.CreateProduction(services =>
            services.PostConfigure<JwtBearerOptions>(AuthenticationExtensions.TenantScheme, options =>
                options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    $"http://127.0.0.1:{port}/.well-known/openid-configuration",
                    new OpenIdConnectConfigurationRetriever(),
                    new HttpDocumentRetriever(new HttpClient { Timeout = TimeSpan.FromSeconds(3) }) { RequireHttps = false })));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health/live", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Shared.CreateToken());

        var clock = Stopwatch.StartNew();
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        clock.Stop();

        await stop.CancelAsync();
        await accepting;
        held.ForEach(static connection => connection.Dispose());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1.5), $"/health/live took {clock.Elapsed.TotalSeconds:F1} s");
    }

    [Fact]
    public async Task A_policy_name_that_is_forgotten_never_grants_access()
    {
        // RequireAuthorization() with no name uses the default policy. It must deny, like the fallback,
        // so omitting a name can never expose an endpoint to every signed-in user.
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();
        var policy = await factory.Services.GetRequiredService<IAuthorizationPolicyProvider>().GetDefaultPolicyAsync();
        var signedIn = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-a")], AuthenticationExtensions.TenantScheme));

        var result = await factory.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(signedIn, null, policy);

        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData(AuthenticationExtensions.TenantScheme)]
    [InlineData(AuthenticationExtensions.PlatformScheme)]
    public async Task Calls_to_the_identity_provider_have_an_explicit_ten_second_timeout(string scheme)
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();

        var options = factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(scheme);

        Assert.Equal(TimeSpan.FromSeconds(10), options.BackchannelTimeout);
    }

    [Theory]
    [InlineData(3600, HttpStatusCode.NotFound)]
    [InlineData(3601, HttpStatusCode.Unauthorized)]
    [InlineData(30 * 24 * 3600, HttpStatusCode.Unauthorized)]
    public async Task Platform_tokens_may_live_at_most_one_hour(int lifetimeSeconds, HttpStatusCode expected)
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();
        var now = DateTime.UtcNow;
        var token = TestIssuer.Platform.CreateToken(
            subject: "operator-a", notBefore: now, expires: now.AddSeconds(lifetimeSeconds), roles: [AuthenticationExtensions.PlatformAdminRole]);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/v1/platform/tenants/not-a-guid", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // A well-formed token reaches the handler, which answers 404 for an id that names no tenant, without a database.
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Tenant_tokens_are_not_subject_to_the_platform_lifetime_cap()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();
        var now = DateTime.UtcNow;
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/v1/me", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestIssuer.Shared.CreateToken(notBefore: now, expires: now.AddHours(8)));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
