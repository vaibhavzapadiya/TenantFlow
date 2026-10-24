namespace TenantFlow.IdentityAccess.Domain.Memberships;

/// <summary>The stable names of a <see cref="MembershipStatus"/>, strict in both directions (see <see cref="MembershipRoleNames"/>).</summary>
public static class MembershipStatusNames
{
    public const string Active = "active";
    public const string Removed = "removed";

    /// <exception cref="InvalidOperationException">The status is not one the domain defines.</exception>
    public static string ToName(this MembershipStatus status) => status switch
    {
        MembershipStatus.Active => Active,
        MembershipStatus.Removed => Removed,
        _ => throw new InvalidOperationException("The membership status is not one the domain defines."),
    };

    /// <exception cref="InvalidOperationException">The name is not a known status name (case matters).</exception>
    public static MembershipStatus Parse(string name) => name switch
    {
        Active => MembershipStatus.Active,
        Removed => MembershipStatus.Removed,
        _ => throw new InvalidOperationException("The stored membership status is not a known status name."),
    };
}
