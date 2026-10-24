namespace TenantFlow.Api.Endpoints.V1.Me.Schemas;

/// <summary>Who the caller is: issuer plus subject (ADR-0009).</summary>
internal sealed record MeResponse(string Issuer, string Subject);
