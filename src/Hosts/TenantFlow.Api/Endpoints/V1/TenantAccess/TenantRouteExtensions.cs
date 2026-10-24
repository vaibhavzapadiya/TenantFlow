using Microsoft.AspNetCore.Mvc;
using TenantFlow.Api.Extensions;

namespace TenantFlow.Api.Endpoints.V1.TenantAccess;

/// <summary>How a route declares that it is a tenant route (ADR-0010). The one way to write one.</summary>
internal static class TenantRouteExtensions
{
    /// <summary>
    /// Requires a tenant-scheme token, an active membership in the tenant named by the <c>{tenantId}</c> route value,
    /// an active tenant, and a role that grants <paramref name="permission"/>. Declaring the permission is mandatory,
    /// so a tenant route cannot be mapped without one.
    /// </summary>
    public static TBuilder RequireTenantPermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        return builder
            .RequireAuthorization(AuthenticationExtensions.TenantAuthenticatedUserPolicy)
            .WithMetadata(new TenantPermissionRequirement(permission))
            // Every tenant route can answer these three boundary denials; documented once, here (ADR-0019).
            .WithMetadata(Problem(StatusCodes.Status401Unauthorized), Problem(StatusCodes.Status403Forbidden), Problem(StatusCodes.Status404NotFound))
            .AddEndpointFilter<TBuilder, TenantAccessFilter>();
    }

    private static ProducesResponseTypeMetadata Problem(int status) => new(status, typeof(ProblemDetails), ["application/problem+json"]);
}
