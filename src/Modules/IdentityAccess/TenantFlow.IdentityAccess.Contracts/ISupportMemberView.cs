namespace TenantFlow.IdentityAccess.Contracts;

/// <summary>
/// The read-only member view a support operator gets through an active support grant (SPEC-0009 criterion 17,
/// ADR-0027 decision 3): membership id, role, status and dates, never an issuer or a subject. The caller has checked the
/// grant for the tenant and committed its access record before it asks.
/// </summary>
public interface ISupportMemberView
{
    /// <summary>Every membership of <paramref name="tenantId"/>, oldest first.</summary>
    Task<IReadOnlyList<SupportMember>> ListAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>A membership as support sees it: no identity of the person.</summary>
public sealed record SupportMember(Guid MembershipId, string Role, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
