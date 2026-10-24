namespace TenantFlow.IdentityAccess.Application.Memberships.Dtos;

/// <summary>One of a caller's own memberships, as <c>GET /v1/me/memberships</c> lists it. Role by its stable name.</summary>
public sealed record MembershipSummary(Guid MembershipId, Guid TenantId, string Role);
