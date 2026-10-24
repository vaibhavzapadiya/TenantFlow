namespace TenantFlow.BuildingBlocks.Abstractions.Errors;

/// <summary>
/// What kind of expected failure an <see cref="AppError"/> is, independent of any transport. The API
/// maps a kind to an HTTP status in one place; Domain and Application never mention HTTP (ADR-0016).
/// </summary>
public enum ErrorKind
{
    /// <summary>The input is invalid (400).</summary>
    Validation = 1,

    /// <summary>The requested resource does not exist, or the caller may not know that it does (404).</summary>
    NotFound = 2,

    /// <summary>The request conflicts with the current state of a resource (409).</summary>
    Conflict = 3,

    /// <summary>The caller is authenticated but not allowed (403).</summary>
    Forbidden = 4,

    /// <summary>The caller's precondition, such as an <c>If-Match</c> version, no longer holds (412).</summary>
    PreconditionFailed = 5,

    /// <summary>The request is well formed but cannot be processed as sent, such as a reused idempotency key (422).</summary>
    UnprocessableContent = 6,

    /// <summary>
    /// The request proves no identity the endpoint accepts (401), such as a webhook whose signature does not verify. Bearer
    /// token failures are answered by the authentication middleware, not through this kind.
    /// </summary>
    Unauthenticated = 7,

    /// <summary>The request body is larger than the operation allows (413).</summary>
    PayloadTooLarge = 8,

    /// <summary>A dependency the operation needs cannot be reached right now; the caller may retry (503).</summary>
    Unavailable = 9,
}
