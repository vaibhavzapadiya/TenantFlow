using Microsoft.AspNetCore.Authorization;

namespace TenantFlow.Api.Middleware;

/// <summary>
/// Lets routing's own answers through the deny-by-default fallback policy. When the path exists but the
/// method or content type does not match, routing selects a synthetic endpoint that answers 405 or 415.
/// Those endpoints carry no authorization metadata, so the fallback policy challenged them and a signed-in
/// caller with a perfectly good token got a 401. Marking just those two endpoints anonymous returns the
/// honest status; paths that match nothing at all stay denied exactly as before (SPEC-0002 criterion 8).
/// </summary>
/// <remarks>
/// The synthetic endpoints are recognised by their display names, which ASP.NET Core does not publish as
/// a contract. <c>ErrorContractTests</c> fail loudly if a framework upgrade changes them (check at the .NET 10 move).
/// </remarks>
internal sealed class RoutingErrorEndpointsMiddleware(RequestDelegate next)
{
    private static readonly EndpointMetadataCollection AllowAnonymous = new(new AllowAnonymousAttribute());

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.GetEndpoint() is { } endpoint && IsRoutingErrorEndpoint(endpoint))
        {
            context.SetEndpoint(new Endpoint(endpoint.RequestDelegate, AllowAnonymous, endpoint.DisplayName));
        }

        return next(context);
    }

    private static bool IsRoutingErrorEndpoint(Endpoint endpoint) =>
        endpoint.Metadata.Count == 0
        && endpoint.DisplayName is { } name
        && (name.StartsWith("405 ", StringComparison.Ordinal) || name.StartsWith("415 ", StringComparison.Ordinal));
}
