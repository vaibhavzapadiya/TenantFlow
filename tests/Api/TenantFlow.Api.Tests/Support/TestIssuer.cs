using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using TenantFlow.Api.Extensions;

namespace TenantFlow.Api.Tests.Support;

/// <summary>
/// In-process OIDC issuers for tests (ADR-0009): one for tenant users and a separate one for platform
/// operators (ADR-0010). Each RSA key is generated per process and never stored. The API under test
/// trusts them through a static configuration, so no metadata is fetched. They also mint deliberately
/// invalid tokens to prove denial.
/// </summary>
internal sealed class TestIssuer : IDisposable
{
    public const string Issuer = "https://issuer.tenantflow.test/realms/tenantflow";
    public const string Audience = "tenantflow-api";
    public const string PlatformIssuer = "https://issuer.tenantflow.test/realms/tenantflow-platform";
    public const string PlatformAudience = "tenantflow-platform-api";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _key;

    private TestIssuer(string scheme, string issuer, string audience)
    {
        Scheme = scheme;
        IssuerUrl = issuer;
        AudienceValue = audience;
        _key = new RsaSecurityKey(_rsa) { KeyId = $"test-signing-key-{scheme}" };
    }

    /// <summary>The tenant-user issuer. One per test process is enough: tokens carry their own subject.</summary>
    public static TestIssuer Shared { get; } = new(AuthenticationExtensions.TenantScheme, Issuer, Audience);

    /// <summary>The platform issuer (platform administrators and support operators).</summary>
    public static TestIssuer Platform { get; } = new(AuthenticationExtensions.PlatformScheme, PlatformIssuer, PlatformAudience);

    public string Scheme { get; }

    public string IssuerUrl { get; }

    public string AudienceValue { get; }

    /// <summary>The settings a test host needs; values are public identifiers, not secrets.</summary>
    public static IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>
    {
        ["Authentication:Tenant:Authority"] = Issuer,
        ["Authentication:Tenant:Audience"] = Audience,
        ["Authentication:Platform:Authority"] = PlatformIssuer,
        ["Authentication:Platform:Audience"] = PlatformAudience,
    };

    /// <summary>Makes the host trust both issuers' keys instead of fetching provider metadata.</summary>
    public static void AttachAllTo(IServiceCollection services)
    {
        Shared.AttachTo(services);
        Platform.AttachTo(services);
    }

    public string CreateToken(
        string? subject = "user-a",
        string? issuer = null,
        string? audience = null,
        DateTime? notBefore = null,
        DateTime? expires = null,
        SigningCredentials? signingCredentials = null,
        IEnumerable<string>? roles = null)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>();
        if (subject is not null)
        {
            claims.Add(new Claim("sub", subject));
        }

        claims.AddRange((roles ?? []).Select(static role => new Claim(AuthenticationExtensions.RolesClaim, role)));

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? IssuerUrl,
            Audience = audience ?? AudienceValue,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = notBefore ?? now,
            NotBefore = notBefore ?? now,
            Expires = expires ?? now.AddMinutes(5),
            SigningCredentials = signingCredentials ?? new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>A platform token for a caller the provider grants the operator role.</summary>
    public string CreateOperatorToken(string subject = "operator-a") =>
        CreateToken(subject, roles: [AuthenticationExtensions.PlatformAdminRole]);

    /// <summary>A platform token with the support operator role only (ADR-0027).</summary>
    public string CreateSupportToken(string subject = "support-a") =>
        CreateToken(subject, roles: [AuthenticationExtensions.SupportOperatorRole]);

    /// <summary>A validly shaped tenant token with <c>alg: none</c> and no signature.</summary>
    public static string CreateUnsignedToken()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        static string Encode(string json) => Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(json));
        return $"{Encode("""{"alg":"none","typ":"JWT"}""")}.{Encode($$"""{"iss":"{{Issuer}}","aud":"{{Audience}}","sub":"user-a","nbf":{{now}},"exp":{{now + 300}}}""")}.";
    }

    /// <summary>The trusted key with another algorithm, for example RS512, which is not on the allow-list.</summary>
    public SigningCredentials TrustedKeyWith(string algorithm) => new(_key, algorithm);

    /// <summary>HS256 keyed with this issuer's public key: the classic algorithm-confusion attack.</summary>
    public SigningCredentials PublicKeyAsHmacSecret() =>
        new(new SymmetricSecurityKey(_rsa.ExportSubjectPublicKeyInfo()), SecurityAlgorithms.HmacSha256);

    public void Dispose() => _rsa.Dispose();

    private void AttachTo(IServiceCollection services)
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = IssuerUrl };
        configuration.SigningKeys.Add(_key);
        services.PostConfigure<JwtBearerOptions>(Scheme, options =>
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration));
    }
}
