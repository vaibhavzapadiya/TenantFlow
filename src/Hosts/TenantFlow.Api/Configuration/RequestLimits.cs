namespace TenantFlow.Api.Configuration;

/// <summary>
/// Limits on what the API will read (blueprint section 9). Every current endpoint takes a small JSON body, so the
/// default is small; an endpoint that legitimately needs more (imports, M8) raises its own limit explicitly.
/// A body over the limit is answered 413 <c>request.payload_too_large</c>.
/// </summary>
internal static class RequestLimits
{
    /// <summary>64 KiB.</summary>
    public const long MaxRequestBodyBytes = 64 * 1024;
}
