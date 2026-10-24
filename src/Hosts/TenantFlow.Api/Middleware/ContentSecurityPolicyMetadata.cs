namespace TenantFlow.Api.Middleware;

/// <summary>
/// Endpoint metadata that replaces the baseline <c>Content-Security-Policy</c> for one endpoint only. The baseline stays
/// on every other response, including a 404 under the same path. Only the Development-only API reference uses it.
/// </summary>
internal sealed record ContentSecurityPolicyMetadata(string Policy);
