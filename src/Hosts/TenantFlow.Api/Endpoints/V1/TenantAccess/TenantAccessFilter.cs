using TenantFlow.Api.Telemetry;
using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Abstractions.Identity;
using TenantFlow.IdentityAccess.Application.TenantAccess;
using TenantFlow.IdentityAccess.Domain.Memberships;
using TenantFlow.Tenants.Contracts;

namespace TenantFlow.Api.Endpoints.V1.TenantAccess;

/// <summary>
/// Runs before every tenant route's handler (ADR-0010; SPEC-0002 criteria 12 to 15). The tenant id in the route only
/// selects a tenant: this filter resolves the caller's active membership in it, checks the tenant is active and the
/// role grants the route's permission, and sets the tenant context. Any failure is an <c>AppException</c> answered by
/// the error pipeline (404 not a member or no such tenant, 403 suspended or missing permission); the handler never runs.
/// </summary>
internal sealed class TenantAccessFilter(TenantAccessResolver resolver, AuthDenialRecorder denials) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;

        // A tenant route with no declared permission is a programming error and must never run unchecked.
        var requirement = http.GetEndpoint()?.Metadata.GetMetadata<TenantPermissionRequirement>()
            ?? throw new InvalidOperationException("A tenant route must declare the permission it requires.");

        // The tenant scheme guarantees both claims (issuer validation and the required subject).
        var caller = new ActorIdentity(http.User.FindFirst("iss")!.Value, http.User.FindFirst("sub")!.Value);
        try
        {
            // A route id that is not a tenant id is denied exactly like an unknown tenant, and counted like one.
            var tenantId = TenantRouteId.Parse(http.Request.RouteValues["tenantId"] as string ?? string.Empty);
            await resolver.ResolveAsync(tenantId, caller, requirement.Permission, http.RequestAborted);
        }
        catch (AppException denial) when (ReasonOf(denial.Error) is { } reason)
        {
            denials.Record(AuthDenialRecorder.TenantLabel, reason);
            throw;
        }

        return await next(context);
    }

    // The three ways a tenant route denies a caller, as telemetry reasons. Anything else is not a denial.
    private static string? ReasonOf(AppError error) => error switch
    {
        _ when error == TenantContractErrors.NotFound => AuthDenialRecorder.NotAMember,
        _ when error == MembershipErrors.TenantSuspended => AuthDenialRecorder.TenantSuspended,
        _ when error == MembershipErrors.PermissionDenied => AuthDenialRecorder.PermissionDenied,
        _ => null,
    };
}
