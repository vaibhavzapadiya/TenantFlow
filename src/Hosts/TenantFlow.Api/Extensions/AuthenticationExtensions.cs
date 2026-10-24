using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TenantFlow.Api.Configuration;
using TenantFlow.Api.Telemetry;
using TenantFlow.BuildingBlocks.Abstractions.Identity;

namespace TenantFlow.Api.Extensions;

/// <summary>
/// Authentication schemes and the deny-by-default authorization baseline (ADR-0009, ADR-0010;
/// SPEC-0002 criteria 5 to 9). Tenant users and platform operators come from separate issuers with
/// separate audiences, so neither token is accepted on the other's routes. Tokens only say who the
/// caller is; tenant authority comes from memberships, never from token claims.
/// </summary>
internal static class AuthenticationExtensions
{
    /// <summary>Tenant users (customers and tenant administrators).</summary>
    public const string TenantScheme = "Tenant";

    /// <summary>Platform operators, on the <c>/platform</c> routes only.</summary>
    public const string PlatformScheme = "Platform";

    /// <summary>Any authenticated tenant user, before a tenant is selected (for example <c>/me</c>).</summary>
    public const string TenantAuthenticatedUserPolicy = "TenantAuthenticatedUser";

    /// <summary>
    /// A platform-scheme caller with the platform administrator role: tenant lifecycle, first administrators, the platform
    /// audit, revoking support grants (SPEC-0009 criterion 15, ADR-0027).
    /// </summary>
    public const string PlatformAdminPolicy = "PlatformAdmin";

    /// <summary>A platform-scheme caller with the support operator role: its own support grants and the support views.</summary>
    public const string SupportOperatorPolicy = "SupportOperator";

    /// <summary>Either platform role, for the one route both may call (ending a grant); the use case checks which.</summary>
    public const string PlatformStaffPolicy = "PlatformStaff";

    /// <summary>Provider-neutral role claim the identity provider is configured to issue (ADR-0010).</summary>
    public const string RolesClaim = "tenantflow_roles";

    public const string PlatformAdminRole = "platform-admin";

    public const string SupportOperatorRole = "support-operator";

    private static readonly string[] Schemes = [TenantScheme, PlatformScheme];

    /// <summary>
    /// The most powerful credentials in the system must be short-lived, and the API cannot see how the
    /// identity provider is configured, so a platform token lasting longer than this is refused.
    /// </summary>
    private static readonly TimeSpan PlatformTokenMaxLifetime = TimeSpan.FromHours(1);

    /// <summary>Explicit timeout for every call to an identity provider (blueprint section 14); the library default is 60 seconds.</summary>
    private static readonly TimeSpan IdentityProviderTimeout = TimeSpan.FromSeconds(10);

    // Asymmetric algorithms only: a symmetric algorithm would let anyone holding the verification
    // key mint tokens, and "none" is never accepted.
    private static readonly string[] AllowedAlgorithms =
        [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.EcdsaSha256];

    public static WebApplicationBuilder AddTenantFlowAuthentication(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IValidateOptions<OidcIssuerOptions>, OidcIssuerOptions.Validator>();
        builder.Services.AddSingleton<AuthDenialRecorder>();
        // No default *authenticate* scheme, on purpose: a default makes UseAuthentication validate a bearer token on
        // every request, including /health/live, which would tie liveness to the identity provider (SPEC-0001).
        // Every policy names the scheme it needs, so tokens are only read where authorization asks for them.
        // The default challenge and forbid schemes only decide how a refusal is written (401 or 403), never read a token.
        var authentication = builder.Services.AddAuthentication(options =>
        {
            options.DefaultChallengeScheme = TenantScheme;
            options.DefaultForbidScheme = TenantScheme;
        });
        foreach (var scheme in Schemes)
        {
            builder.Services.AddOptions<OidcIssuerOptions>(scheme)
                .BindConfiguration(OidcIssuerOptions.SectionFor(scheme))
                .ValidateOnStart();
            authentication.AddJwtBearer(scheme);
            builder.Services.AddOptions<JwtBearerOptions>(scheme)
                .Configure<IOptionsMonitor<OidcIssuerOptions>>((jwt, issuers) => Configure(
                    jwt, issuers.Get(scheme), scheme == PlatformScheme ? PlatformTokenMaxLifetime : null));
        }

        builder.Services.AddSingleton<IValidateOptions<IssuerSeparation>, IssuerSeparation.Validator>();
        builder.Services.AddOptions<IssuerSeparation>()
            .Configure<IOptionsMonitor<OidcIssuerOptions>>((separation, issuers) =>
            {
                separation.Tenant = issuers.Get(TenantScheme);
                separation.Platform = issuers.Get(PlatformScheme);
            })
            .ValidateOnStart();

        // Two ways to forget a policy, both closed: an endpoint with no authorization metadata gets the fallback
        // policy, and RequireAuthorization() with no name gets the default policy. Both deny everyone, so
        // forgetting never grants access. Unmatched paths are covered too, so routes cannot be probed.
        //
        // The policy names no scheme, so it never reads a token. The authorization middleware evaluates the fallback
        // even for AllowAnonymous endpoints; naming a scheme here made /health/live validate any bearer token it was
        // sent, and wait for the identity provider to do it. The price: every caller, signed in or not, is answered
        // 401 on a path that has no policy, which tells a probing caller even less.
        var denyAll = new AuthorizationPolicyBuilder()
            .RequireAssertion(static _ => false)
            .Build();
        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(denyAll)
            .SetDefaultPolicy(denyAll)
            .AddPolicy(TenantAuthenticatedUserPolicy, policy => policy
                .AddAuthenticationSchemes(TenantScheme)
                .RequireAuthenticatedUser())
            // Neither role implies the other (ADR-0027 decision 1).
            .AddPolicy(PlatformAdminPolicy, policy => policy
                .AddAuthenticationSchemes(PlatformScheme)
                .RequireAuthenticatedUser()
                .RequireClaim(RolesClaim, PlatformAdminRole))
            .AddPolicy(SupportOperatorPolicy, policy => policy
                .AddAuthenticationSchemes(PlatformScheme)
                .RequireAuthenticatedUser()
                .RequireClaim(RolesClaim, SupportOperatorRole))
            .AddPolicy(PlatformStaffPolicy, policy => policy
                .AddAuthenticationSchemes(PlatformScheme)
                .RequireAuthenticatedUser()
                .RequireClaim(RolesClaim, PlatformAdminRole, SupportOperatorRole));

        return builder;
    }

    /// <summary>
    /// True when the token's lifetime (expiry minus issue time, or minus not-before when it has no issue time)
    /// is within the limit. A token whose lifetime cannot be worked out is refused: the check fails closed.
    /// </summary>
    private static bool LivesNoLongerThan(SecurityToken token, TimeSpan limit)
    {
        if (token is not JsonWebToken jwt)
        {
            return false;
        }

        var start = jwt.IssuedAt != DateTime.MinValue ? jwt.IssuedAt : jwt.ValidFrom;
        return start != DateTime.MinValue && jwt.ValidTo != DateTime.MinValue && jwt.ValidTo - start <= limit;
    }

    private static void Configure(JwtBearerOptions jwt, OidcIssuerOptions issuer, TimeSpan? maxTokenLifetime)
    {
        jwt.Authority = issuer.Authority;
        jwt.Audience = issuer.Audience;
        jwt.RequireHttpsMetadata = issuer.RequireHttpsMetadata;
        jwt.BackchannelTimeout = IdentityProviderTimeout;
        // Keep claim names as issued ("sub", "iss"), and never describe validation failures to callers.
        jwt.MapInboundClaims = false;
        jwt.IncludeErrorDetails = false;
        jwt.SaveToken = false;
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            ValidAlgorithms = AllowedAlgorithms,
            NameClaimType = "sub",
        };
        jwt.Events = new JwtBearerEvents
        {
            // Identity is issuer plus subject (ADR-0009); a token without a subject identifies nobody, and a subject
            // longer than the column that will store it is refused here instead of failing later as a 500.
            OnTokenValidated = context =>
            {
                var subject = context.Principal?.FindFirst("sub")?.Value;
                if (string.IsNullOrWhiteSpace(subject))
                {
                    context.Fail("The token has no subject.");
                }
                else if (subject.Length > IdentityLimits.SubjectMaxLength)
                {
                    context.Fail("The token subject is too long.");
                }
                else if (maxTokenLifetime is { } limit && !LivesNoLongerThan(context.SecurityToken, limit))
                {
                    context.Fail("The token lives longer than this scheme allows.");
                }

                return Task.CompletedTask;
            },
            // Status only: UseStatusCodePages writes the Problem Details body, with its code and trace id, in
            // one place (ADR-0016). The reason a token was rejected is never described to the caller.
            OnChallenge = static context =>
            {
                // No failure means no token was sent; a failure means one was sent and refused.
                context.HttpContext.RequestServices.GetRequiredService<AuthDenialRecorder>().Record(
                    AuthDenialRecorder.LabelOf(context.Scheme.Name),
                    context.AuthenticateFailure is null ? AuthDenialRecorder.NoToken : AuthDenialRecorder.InvalidToken);
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = JwtBearerDefaults.AuthenticationScheme;
                return Task.CompletedTask;
            },
            OnForbidden = static context =>
            {
                // The only policy that forbids an authenticated caller is the platform operator role requirement.
                context.HttpContext.RequestServices.GetRequiredService<AuthDenialRecorder>().Record(
                    AuthDenialRecorder.LabelOf(context.Scheme.Name), AuthDenialRecorder.NotOperator);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            },
        };
    }
}
