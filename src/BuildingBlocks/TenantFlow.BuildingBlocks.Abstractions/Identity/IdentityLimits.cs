namespace TenantFlow.BuildingBlocks.Abstractions.Identity;

/// <summary>
/// Size limits for the identity claims TenantFlow stores (audit records now, memberships next). One
/// definition, so token validation, startup validation, and the database columns cannot drift apart.
/// </summary>
public static class IdentityLimits
{
    /// <summary>OpenID Connect limits <c>sub</c> to 255 ASCII characters.</summary>
    public const int SubjectMaxLength = 255;

    /// <summary>Room for the issuer URL; the configured authority must fit.</summary>
    public const int IssuerMaxLength = 500;
}
