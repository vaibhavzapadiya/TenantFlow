using Microsoft.Extensions.Options;
using TenantFlow.BuildingBlocks.Abstractions.Identity;

namespace TenantFlow.Api.Configuration;

/// <summary>
/// One trusted OIDC issuer (ADR-0009), bound per scheme from <c>Authentication:{Scheme}</c>. Neither
/// value is a secret: tokens are verified against the issuer's published keys.
/// </summary>
internal sealed class OidcIssuerOptions
{
    public string? Authority { get; set; }

    public string? Audience { get; set; }

    /// <summary>Only the local Keycloak (Development) serves its metadata over plain HTTP.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    public static string SectionFor(string scheme) => $"Authentication:{scheme}";

    /// <summary>Validates each scheme's options at startup. Messages name the key, never the value (ADR-0008).</summary>
    internal sealed class Validator : IValidateOptions<OidcIssuerOptions>
    {
        public ValidateOptionsResult Validate(string? name, OidcIssuerOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            var section = SectionFor(name ?? string.Empty);
            var failures = new List<string>();
            if (string.IsNullOrWhiteSpace(options.Authority))
            {
                failures.Add($"{section}:Authority is required: the OIDC issuer URL this scheme trusts.");
            }

            if (string.IsNullOrWhiteSpace(options.Audience))
            {
                failures.Add($"{section}:Audience is required: the audience its access tokens are issued for.");
            }

            if (options.Authority is { } authorityValue && authorityValue.Length > IdentityLimits.IssuerMaxLength)
            {
                failures.Add($"{section}:Authority must be at most {IdentityLimits.IssuerMaxLength} characters: it is stored in audit records.");
            }

            // Signing keys fetched over plain HTTP could be replaced in transit, so HTTP metadata is
            // allowed only from this machine.
            if (!options.RequireHttpsMetadata
                && !string.IsNullOrWhiteSpace(options.Authority)
                && !(Uri.TryCreate(options.Authority, UriKind.Absolute, out var authority) && authority.IsLoopback))
            {
                failures.Add($"{section}:RequireHttpsMetadata can be false only for a loopback authority (local development).");
            }

            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }
    }
}
