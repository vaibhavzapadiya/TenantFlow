using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TenantFlow.BuildingBlocks.Observability;

/// <summary>
/// Health endpoint conventions for every TenantFlow host (SPEC-0001; blueprint section 16).
/// </summary>
public static class HealthEndpoints
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";

    /// <summary>Tag for checks that decide readiness. Liveness runs no checks at all.</summary>
    public const string ReadyTag = "ready";

    /// <summary>
    /// Maps <c>/health/live</c> (the process can serve requests; never depends on another system)
    /// and <c>/health/ready</c> (every check tagged <see cref="ReadyTag"/> passes). Both return only
    /// the status word: 200 for Healthy or Degraded, 503 for Unhealthy. Both stay anonymous under a
    /// deny-by-default authorization policy, because platforms probe them without credentials.
    /// </summary>
    public static IEndpointRouteBuilder MapTenantFlowHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapHealthChecks(LivePath, new HealthCheckOptions
        {
            Predicate = static _ => false,
            ResponseWriter = WriteStatusOnlyAsync,
        }).AllowAnonymous();
        endpoints.MapHealthChecks(ReadyPath, new HealthCheckOptions
        {
            Predicate = static registration => registration.Tags.Contains(ReadyTag),
            ResponseWriter = WriteStatusOnlyAsync,
        }).AllowAnonymous();

        return endpoints;
    }

    // Anonymous probes see only the status word. Exception text, hosts, and dependency names stay
    // in the health report and logs; protected diagnostics come later (M9/M10).
    private static Task WriteStatusOnlyAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "text/plain";
        return context.Response.WriteAsync(report.Status.ToString(), context.RequestAborted);
    }
}
