namespace TenantFlow.Api.Middleware;

/// <summary>
/// The baseline response headers for a JSON API (OWASP REST Security Cheat Sheet; blueprint section 13; ADR-0017).
/// They are set as the response starts, so every response carries them, including errors written after the exception
/// handler has cleared the response. HSTS and HTTPS redirection belong to the edge that terminates TLS.
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.OnStarting(static state =>
        {
            var context = (HttpContext)state;
            var headers = context.Response.Headers;
            // A body is never sniffed into another content type (a Problem Details body is never rendered as HTML).
            headers.XContentTypeOptions = "nosniff";
            // Tenant data must not be stored by a shared cache or a browser history. An endpoint that is safe to cache
            // must say so explicitly and be reviewed.
            headers.CacheControl = "no-store";
            headers["Referrer-Policy"] = "no-referrer";
            // The endpoint is chosen by now; only an endpoint that carries its own reviewed policy replaces the baseline.
            headers.ContentSecurityPolicy = context.GetEndpoint()?.Metadata.GetMetadata<ContentSecurityPolicyMetadata>()?.Policy
                ?? "default-src 'none'; frame-ancestors 'none'";
            return Task.CompletedTask;
        }, context);
        return next(context);
    }
}
