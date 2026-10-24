using TenantFlow.Api.ErrorHandling;

namespace TenantFlow.Api.Middleware;

/// <summary>
/// Answers the failures that are the caller's doing (a rule was broken, input was invalid or unreadable,
/// a resource is missing or in conflict) with Problem Details. It sits inside <c>UseExceptionHandler</c>:
/// these exceptions are normal outcomes and stop here, so they are never logged as errors; anything this
/// middleware does not recognise passes on to the exception handler, which logs it and answers a generic 500.
/// </summary>
internal sealed class ExpectedFailureMiddleware(RequestDelegate next, IProblemDetailsService problems)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await next(context);
        }
        catch (Exception exception) when (!context.Response.HasStarted && ProblemMapper.TryCreate(exception, out var problem))
        {
            context.Response.StatusCode = problem.Status ?? StatusCodes.Status400BadRequest;
            await problems.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = problem,
                Exception = exception,
            });
        }
    }
}
