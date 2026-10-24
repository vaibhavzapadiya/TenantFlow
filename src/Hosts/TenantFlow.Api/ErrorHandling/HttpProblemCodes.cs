namespace TenantFlow.Api.ErrorHandling;

/// <summary>
/// Codes and titles for failures that have no module error of their own: the framework answered with a
/// bare status (401 from the authentication challenge, 405 from routing, 404 from nothing matching).
/// Module failures carry their own <c>AppError</c> code instead (ADR-0016). Codes follow the same
/// <c>area.reason</c> convention and are just as stable.
/// </summary>
internal static class HttpProblemCodes
{
    public const string ValidationFailed = "request.validation_failed";
    public const string Malformed = "request.malformed";
    public const string Unexpected = "server.unexpected";

    /// <summary>Prefix of the problem <c>type</c> URI: a stable, non-resolvable URN per code.</summary>
    public const string TypePrefix = "urn:tenantflow:problem:";

    public static (string Code, string Title) For(int status) => status switch
    {
        StatusCodes.Status400BadRequest => (Malformed, "The request could not be read."),
        StatusCodes.Status401Unauthorized => ("authentication.required", "Authentication is required."),
        StatusCodes.Status403Forbidden => ("authorization.forbidden", "You are not allowed to perform this action."),
        StatusCodes.Status404NotFound => ("resource.not_found", "The resource does not exist."),
        StatusCodes.Status405MethodNotAllowed => ("request.method_not_allowed", "The HTTP method is not supported for this resource."),
        StatusCodes.Status408RequestTimeout => ("request.timeout", "The request took too long to send."),
        StatusCodes.Status409Conflict => ("request.conflict", "The request conflicts with the current state."),
        StatusCodes.Status413PayloadTooLarge => ("request.payload_too_large", "The request body is too large."),
        StatusCodes.Status415UnsupportedMediaType => ("request.unsupported_media_type", "The request content type is not supported."),
        StatusCodes.Status429TooManyRequests => ("request.rate_limited", "Too many requests. Try again later."),
        StatusCodes.Status500InternalServerError => (Unexpected, "An unexpected error occurred."),
        >= 400 and < 500 => ("request.rejected", "The request was rejected."),
        _ => ("server.error", "The server could not complete the request."),
    };
}
