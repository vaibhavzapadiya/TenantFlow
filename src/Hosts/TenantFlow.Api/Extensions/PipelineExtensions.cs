using TenantFlow.Api.Middleware;

namespace TenantFlow.Api.Extensions;

/// <summary>
/// The API's middleware pipeline, in one place and in order (ADR-0017). Outermost first; each step only sees what the
/// steps before it let through. Routing is explicit so it runs inside the exception handler: left implicit, ASP.NET Core
/// inserts it first, and a fault in routing would escape as a raw exception.
/// </summary>
internal static class PipelineExtensions
{
    public static WebApplication UseTenantFlowPipeline(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // 1. Outermost, so no step below can answer without them. (Registering them later still works for faults, because
        //    they are added as the response starts; the order is kept for the guarantee, and no test can tell the difference.)
        app.UseMiddleware<SecurityHeadersMiddleware>();

        // 2. Error handling (ADR-0016): unexpected faults are logged and answered with a generic 500; a bare 4xx/5xx
        //    gets a Problem Details body.
        app.UseExceptionHandler();
        app.UseStatusCodePages();

        // 3. Routing chooses the endpoint every later step reads.
        app.UseRouting();

        // 4. Caller mistakes (AppException, validation, unreadable bodies) are answered here and never logged as
        //    errors; routing's own 405 and 415 are let through authorization.
        app.UseMiddleware<ExpectedFailureMiddleware>();
        app.UseMiddleware<RoutingErrorEndpointsMiddleware>();

        // 5. Authentication and authorization (ADR-0009, ADR-0010). Not here, on purpose: HTTPS redirection and HSTS
        //    (the edge terminates TLS), CORS (no browser client exists; adding one needs a reviewed policy), rate
        //    limiting (needs the trusted-proxy and per-tenant decisions of M9), and OpenAPI exposure (M3).
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }
}
