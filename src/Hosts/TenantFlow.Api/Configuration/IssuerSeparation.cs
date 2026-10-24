using Microsoft.Extensions.Options;
using TenantFlow.Api.Extensions;

namespace TenantFlow.Api.Configuration;

/// <summary>
/// Checked at startup: tenant users and platform operators must come from separate issuers with separate
/// audiences (ADR-0010). Configuring both schemes identically would silently collapse the boundary, so the
/// host refuses to start. Messages name the keys, never the values (ADR-0008).
/// </summary>
internal sealed class IssuerSeparation
{
    public OidcIssuerOptions Tenant { get; set; } = new();

    public OidcIssuerOptions Platform { get; set; } = new();

    internal sealed class Validator : IValidateOptions<IssuerSeparation>
    {
        public ValidateOptionsResult Validate(string? name, IssuerSeparation options)
        {
            ArgumentNullException.ThrowIfNull(options);
            var failures = new List<string>();
            if (Same(options.Tenant.Authority, options.Platform.Authority))
            {
                failures.Add(Describe("Authority"));
            }

            if (Same(options.Tenant.Audience, options.Platform.Audience))
            {
                failures.Add(Describe("Audience"));
            }

            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }

        // Case and a trailing slash do not make two issuers different. A missing value is reported by the
        // per-scheme validator, not here.
        private static bool Same(string? tenant, string? platform) =>
            !string.IsNullOrWhiteSpace(tenant)
            && !string.IsNullOrWhiteSpace(platform)
            && string.Equals(tenant.Trim().TrimEnd('/'), platform.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

        private static string Describe(string setting) =>
            $"{OidcIssuerOptions.SectionFor(AuthenticationExtensions.PlatformScheme)}:{setting} must differ from "
            + $"{OidcIssuerOptions.SectionFor(AuthenticationExtensions.TenantScheme)}:{setting}: tenant users and platform operators come from separate issuers (ADR-0010).";
    }
}
