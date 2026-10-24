using TenantFlow.Api.Configuration;

namespace TenantFlow.Api.Extensions;

/// <summary>Host-level settings for the API: Kestrel limits and dependency-injection validation (ADR-0017).</summary>
internal static class HostConfigurationExtensions
{
    public static WebApplicationBuilder ConfigureTenantFlowHost(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Do not advertise the server implementation, and cap what the API will read (blueprint section 9).
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = RequestLimits.MaxRequestBodyBytes;
        });

        // Fail fast on dependency-injection mistakes in every environment, not just Development:
        // - ValidateOnBuild: every registration must be constructible when the host starts.
        // - ValidateScopes: scoped services (for example a DbContext or tenant context) can never be
        //   resolved from the root provider, which would silently turn them into singletons.
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });
        return builder;
    }
}
