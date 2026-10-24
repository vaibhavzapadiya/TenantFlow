namespace TenantFlow.Api.Endpoints.V1;

/// <summary>
/// URL-segment API versioning (blueprint section 9). Every business endpoint is mapped on a version
/// group, so a contract change that breaks clients ships as a new group (<c>/v2</c>) beside the old
/// one, with its own endpoint definitions where they differ. Health probes stay unversioned: platforms
/// call them without knowing the API's version (ADR-0015).
/// </summary>
internal static class ApiVersions
{
    /// <summary>Current and only version.</summary>
    public const string V1 = "v1";

    /// <summary>The <c>/v1</c> route group; map every version-one endpoint on it.</summary>
    public static RouteGroupBuilder MapV1(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapGroup($"/{V1}");
    }
}
