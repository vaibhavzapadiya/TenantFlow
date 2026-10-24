using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.ImportsExports.Domain.Jobs;

namespace TenantFlow.Api.Endpoints.V1.Tenants.ImportsExports;

/// <summary>Reads a job id from a route: only the canonical form can name one, anything else is an unknown job.</summary>
internal static class JobRouteId
{
    /// <exception cref="AppException"><see cref="JobErrors.NotFound"/> when the value is not a job id.</exception>
    public static Guid Parse(string value) => RouteId.Parse(value, JobErrors.NotFound);
}
