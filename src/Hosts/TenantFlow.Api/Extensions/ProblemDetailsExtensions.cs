using System.Diagnostics;
using TenantFlow.Api.ErrorHandling;

namespace TenantFlow.Api.Extensions;

/// <summary>
/// The one place that shapes every error body (ADR-0016): RFC 9457 Problem Details with a stable
/// <c>code</c>, a <c>type</c> URN derived from it, and a <c>traceId</c> that is the request's trace id,
/// whether the problem came from a module, the authentication challenge, routing, or an unexpected fault.
/// </summary>
internal static class ProblemDetailsExtensions
{
    public static IServiceCollection AddTenantFlowProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(options => options.CustomizeProblemDetails = Customize);

        // Minimal APIs throw on a malformed or empty body only in Development unless told otherwise,
        // which made the same request a 500 in Development and an empty 400 in Production. Throwing
        // everywhere lets ExpectedFailureMiddleware answer identically in every environment.
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        return services;
    }

    private static void Customize(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        var status = problem.Status ?? context.HttpContext.Response.StatusCode;
        problem.Status = status;

        // A module error already set its code and title; anything else gets the code for its status.
        if (!problem.Extensions.TryGetValue("code", out var existing) || existing is not string code)
        {
            var (statusCode, title) = HttpProblemCodes.For(status);
            code = statusCode;
            problem.Extensions["code"] = code;
            problem.Title = title;
        }

        problem.Type = HttpProblemCodes.TypePrefix + code;
        problem.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;
    }
}
