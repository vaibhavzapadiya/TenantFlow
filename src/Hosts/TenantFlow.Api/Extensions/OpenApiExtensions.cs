using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using TenantFlow.Api.Configuration;
using TenantFlow.Api.Middleware;

namespace TenantFlow.Api.Extensions;

/// <summary>
/// The OpenAPI document of <c>/v1</c> (ADR-0019), generated from the endpoints themselves. It is served only when
/// <c>ApiDocumentation:Enabled</c> is true (Development and the contract test), so a deployed API does not publish its
/// route map by default. The committed snapshot in <c>docs/api/openapi-v1.json</c> is the reviewed contract.
/// </summary>
internal static class OpenApiExtensions
{
    public const string EnabledKey = "ApiDocumentation:Enabled";
    public const string DocumentPath = "/openapi/v1.json";
    public const string ReferencePathPrefix = "/scalar";

    /// <summary>The reference page's sign-in schemes (SPEC-0014): one per trusted issuer, never in the document.</summary>
    public const string TenantSignInScheme = "TenantUser";

    public const string PlatformSignInScheme = "PlatformOperator";

    /// <summary>
    /// The policy of the Development-only reference page, and of no other response. Its scripts and assets come from the
    /// package itself (no CDN), so nothing loads from another origin; the page's inline bootstrap script and the styles
    /// the UI injects at run time need <c>'unsafe-inline'</c>, which is why the policy stays on this endpoint only.
    /// </summary>
    public const string ReferenceContentSecurityPolicy =
        "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "font-src 'self' data:; connect-src 'self'; worker-src 'self' blob:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    /// <summary>
    /// The reference page's policy with its sign-in (SPEC-0014): the page exchanges the authorization code at each trusted
    /// issuer's token endpoint, so <c>connect-src</c> gains exactly those origins and nothing else.
    /// </summary>
    public static string ReferenceContentSecurityPolicyFor(IEnumerable<string> issuerOrigins) =>
        ReferenceContentSecurityPolicy.Replace(
            "connect-src 'self';",
            $"connect-src {string.Join(' ', ["'self'", .. issuerOrigins.Distinct(StringComparer.Ordinal)])};",
            StringComparison.Ordinal);

    public static IServiceCollection AddTenantFlowOpenApi(this IServiceCollection services) =>
        services.AddOpenApi("v1", options =>
        {
            // .NET 10 copies XML doc comments into the document as written, so a CRLF working tree would publish a
            // different contract from an LF one. Normalising keeps the snapshot identical on every machine (SPEC-0015).
            options.AddSchemaTransformer((schema, _, _) =>
            {
                schema.Description = NormalizeNewlines(schema.Description);
                return Task.CompletedTask;
            });
            options.AddOperationTransformer((operation, _, _) =>
            {
                operation.Summary = NormalizeNewlines(operation.Summary);
                operation.Description = NormalizeNewlines(operation.Description);
                return Task.CompletedTask;
            });
            options.AddDocumentTransformer((document, context, _) =>
            {
                document.Info = new OpenApiInfo { Title = "TenantFlow API", Version = "v1" };
                // The test host's address must not leak into the reviewed snapshot.
                document.Servers = [];
                // SPEC-0014: the reference page's sign-in, on a developer's machine only. The reviewed snapshot is generated in
                // Production, so the published contract never carries a client id or an issuer URL.
                if (context.ApplicationServices.GetRequiredService<IHostEnvironment>().IsDevelopment())
                {
                    var issuers = context.ApplicationServices.GetRequiredService<IOptionsMonitor<OidcIssuerOptions>>();
                    document.Components ??= new OpenApiComponents();
                    // Microsoft.OpenApi 2.x leaves the collection null until something is added to it.
                    document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
                    document.Components.SecuritySchemes[TenantSignInScheme] =
                        SignInScheme("Tenant user (realm tenantflow)", issuers.Get(AuthenticationExtensions.TenantScheme).Authority!, "tenantflow-api-docs");
                    document.Components.SecuritySchemes[PlatformSignInScheme] =
                        SignInScheme("Platform operator (realm tenantflow-platform)", issuers.Get(AuthenticationExtensions.PlatformScheme).Authority!, "tenantflow-platform-api-docs");
                }

                return Task.CompletedTask;
            });
        });

    private static string? NormalizeNewlines(string? text) =>
        text is null ? null : text.ReplaceLineEndings("\n");

    public static WebApplication MapTenantFlowOpenApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Configuration.GetValue<bool>(EnabledKey))
        {
            // The document describes routes, not data; it is anonymous so tools can read it without a token.
            app.MapOpenApi(DocumentPath).AllowAnonymous();

            // The interactive reference over the same document, on a developer's machine only, whatever the setting says.
            if (app.Environment.IsDevelopment())
            {
                var issuers = app.Services.GetRequiredService<IOptionsMonitor<OidcIssuerOptions>>();
                var tenant = issuers.Get(AuthenticationExtensions.TenantScheme).Authority!;
                var platform = issuers.Get(AuthenticationExtensions.PlatformScheme).Authority!;

                app.MapScalarApiReference(ReferencePathPrefix, options => options
                        .WithTitle("TenantFlow API")
                        .WithOpenApiRoutePattern(DocumentPath)
                        .AddDocument("v1")
                        // Nothing leaves the machine: no hosted fonts, telemetry, AI agent or MCP endpoint.
                        .DisableDefaultFonts()
                        .DisableTelemetry()
                        .DisableAgent()
                        .DisableMcp()
                        // SPEC-0014: sign in at the local Keycloak. The token stays in the page's memory: persistence is
                        // off by default and must not be enabled (a token in localStorage outlives the tab).
                        .AddSignIn(TenantSignInScheme, "Tenant user (realm tenantflow)", tenant, "tenantflow-api-docs")
                        .AddSignIn(PlatformSignInScheme, "Platform operator (realm tenantflow-platform)", platform, "tenantflow-platform-api-docs")
                        .AddPreferredSecuritySchemes([TenantSignInScheme]))
                    .AllowAnonymous()
                    .WithMetadata(new ContentSecurityPolicyMetadata(ReferenceContentSecurityPolicyFor([OriginOf(tenant), OriginOf(platform)])));
            }
        }

        return app;
    }

    // An authorization code flow with PKCE against one Keycloak realm, through its public docs client (no secret).
    private static ScalarOptions AddSignIn(this ScalarOptions options, string scheme, string description, string authority, string clientId) =>
        options.AddOAuth2Authentication(scheme, oauth => oauth
            .WithDescription(description)
            .WithFlows(flows => flows.WithAuthorizationCode(flow =>
            {
                flow.ClientId = clientId;
                flow.AuthorizationUrl = $"{authority}/protocol/openid-connect/auth";
                flow.TokenUrl = $"{authority}/protocol/openid-connect/token";
                flow.Pkce = Pkce.Sha256;
                flow.SelectedScopes = ["openid"];
            }))
            .WithDefaultScopes(["openid"]));

    private static OpenApiSecurityScheme SignInScheme(string description, string authority, string clientId) => new()
    {
        Type = SecuritySchemeType.OAuth2,
        Description = description,
        Flows = new OpenApiOAuthFlows
        {
            AuthorizationCode = new OpenApiOAuthFlow
            {
                AuthorizationUrl = new Uri($"{authority}/protocol/openid-connect/auth"),
                TokenUrl = new Uri($"{authority}/protocol/openid-connect/token"),
                Scopes = new Dictionary<string, string> { ["openid"] = "Sign in" },
                // Scalar pre-fills the public client from this extension.
                Extensions = new Dictionary<string, IOpenApiExtension> { ["x-scalar-client-id"] = new JsonNodeExtension(clientId) },
            },
        },
    };

    private static string OriginOf(string authority) => new Uri(authority).GetLeftPart(UriPartial.Authority);
}
