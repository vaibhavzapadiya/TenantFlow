using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace TenantFlow.Api.Tests.Authentication;

/// <summary>
/// SPEC-0002 criteria 5 to 8 (ADR-0009, ADR-0010): only valid tokens from the configured issuer and
/// audience authenticate, rejections reveal nothing, and endpoints without a policy are denied.
/// </summary>
public sealed class AuthenticationTests
{
    private const string SentinelValue = "sentinel-value-that-must-never-appear-in-errors";

    public enum InvalidToken
    {
        Missing,
        WrongIssuer,
        WrongAudience,
        UnknownSigningKey,
        AlgorithmNone,
        SymmetricAlgorithm,
        PublicKeyAsHmacSecret,
        AlgorithmNotAllowed,
        Expired,
        NotYetValid,
        NoSubject,
    }

    [Fact]
    public async Task Valid_token_reaches_me_with_its_issuer_and_subject()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, "/v1/me", TestIssuer.Shared.CreateToken(subject: "user-a"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<MeResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(new MeResponse(TestIssuer.Issuer, "user-a"), me);
    }

    [Theory]
    [MemberData(nameof(InvalidTokens))]
    public async Task Invalid_token_is_rejected_with_401_and_reveals_nothing(InvalidToken kind)
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();
        var token = Create(kind);

        using var response = await SendAsync(client, "/v1/me", token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"status\":401", body, StringComparison.Ordinal);
        if (token is not null)
        {
            Assert.DoesNotContain(token, body, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Authentication:Tenant:Authority")]
    [InlineData("Authentication:Tenant:Audience")]
    public async Task Host_refuses_to_start_without_a_tenant_authentication_setting(string key)
    {
        await using var factory = ApiFactory.CreateProduction(
            settings: new Dictionary<string, string> { [key] = string.Empty, ["Sentinel:Secret"] = SentinelValue });

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        var text = exception.ToString();
        Assert.Contains(key, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelValue, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejection_keeps_its_status_when_the_caller_does_not_accept_json()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/v1/me", UriKind.Relative));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("http://idp.example.com/realms/tenantflow")]
    [InlineData("https://idp.example.com/realms/tenantflow")]
    public async Task Host_refuses_http_metadata_for_a_non_loopback_authority(string authority)
    {
        await using var factory = ApiFactory.CreateProduction(settings: new Dictionary<string, string>
        {
            ["Authentication:Tenant:Authority"] = authority,
            ["Authentication:Tenant:RequireHttpsMetadata"] = "false",
        });

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        Assert.Contains("Authentication:Tenant:RequireHttpsMetadata", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_allows_http_metadata_for_a_loopback_authority()
    {
        await using var factory = ApiFactory.CreateProduction(settings: new Dictionary<string, string>
        {
            ["Authentication:Tenant:Authority"] = "http://localhost:8080/realms/tenantflow",
            ["Authentication:Tenant:RequireHttpsMetadata"] = "false",
        });

        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_path_without_a_policy_is_a_401_for_everyone_and_the_token_is_never_read()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();

        // No endpoint matches this path, so only the fallback policy applies.
        using var anonymous = await SendAsync(client, "/no-policy-probe", token: null);
        using var signedIn = await SendAsync(client, "/no-policy-probe", TestIssuer.Shared.CreateToken());

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        // The fallback policy names no scheme (it must not read tokens, or /health/live would wait for the
        // identity provider), so a valid token is answered exactly like no token at all.
        Assert.Equal(HttpStatusCode.Unauthorized, signedIn.StatusCode);
        Assert.Equal("application/problem+json", signedIn.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoints_stay_anonymous(string path)
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, path, token: null);

        // Live is 200 and ready is 503 (PostgreSQL unreachable): what matters is no 401 or 403.
        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable,
            $"{path} returned {(int)response.StatusCode}");
    }

    public static TheoryData<InvalidToken> InvalidTokens() => [.. Enum.GetValues<InvalidToken>()];

    private static string? Create(InvalidToken kind)
    {
        var issuer = TestIssuer.Shared;
        var now = DateTime.UtcNow;
        return kind switch
        {
            InvalidToken.Missing => null,
            InvalidToken.WrongIssuer => issuer.CreateToken(issuer: "https://attacker.example/realms/tenantflow"),
            InvalidToken.WrongAudience => issuer.CreateToken(audience: "another-api"),
            InvalidToken.UnknownSigningKey => CreateWithUnknownKey(issuer),
            InvalidToken.AlgorithmNone => TestIssuer.CreateUnsignedToken(),
            InvalidToken.SymmetricAlgorithm => issuer.CreateToken(
                signingCredentials: new SigningCredentials(new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)), SecurityAlgorithms.HmacSha256)),
            InvalidToken.PublicKeyAsHmacSecret => issuer.CreateToken(signingCredentials: issuer.PublicKeyAsHmacSecret()),
            InvalidToken.AlgorithmNotAllowed => issuer.CreateToken(signingCredentials: issuer.TrustedKeyWith(SecurityAlgorithms.RsaSha512)),
            InvalidToken.Expired => issuer.CreateToken(notBefore: now.AddMinutes(-10), expires: now.AddMinutes(-5)),
            InvalidToken.NotYetValid => issuer.CreateToken(notBefore: now.AddMinutes(5), expires: now.AddMinutes(10)),
            InvalidToken.NoSubject => issuer.CreateToken(subject: null),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    // Same key id as the trusted key, different key material.
    private static string CreateWithUnknownKey(TestIssuer issuer)
    {
        using var unknown = RSA.Create(2048);
        return issuer.CreateToken(signingCredentials: new SigningCredentials(
            new RsaSecurityKey(unknown) { KeyId = "test-signing-key-Tenant" }, SecurityAlgorithms.RsaSha256));
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed record MeResponse(string Issuer, string Subject);
}
