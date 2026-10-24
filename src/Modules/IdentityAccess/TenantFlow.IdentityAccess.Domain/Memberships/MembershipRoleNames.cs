namespace TenantFlow.IdentityAccess.Domain.Memberships;

/// <summary>
/// The stable names a <see cref="MembershipRole"/> has outside the domain: the database and the API both use these,
/// so the mapping exists once. A name from a caller is checked with <see cref="TryParse"/>; a name read from storage
/// goes through <see cref="Parse"/>, which fails loudly, because a stored value nobody recognises is data corruption
/// and must never be read as another role (for example a customer silently becoming an administrator).
/// </summary>
public static class MembershipRoleNames
{
    public const string TenantAdmin = "tenant-admin";
    public const string Customer = "customer";

    /// <exception cref="InvalidOperationException">The role is not one the domain defines.</exception>
    public static string ToName(this MembershipRole role) => role switch
    {
        MembershipRole.TenantAdmin => TenantAdmin,
        MembershipRole.Customer => Customer,
        _ => throw new InvalidOperationException("The membership role is not one the domain defines."),
    };

    /// <summary>Case matters: "Customer" is not a role name.</summary>
    public static bool TryParse(string? name, out MembershipRole role)
    {
        switch (name)
        {
            case TenantAdmin:
                role = MembershipRole.TenantAdmin;
                return true;
            case Customer:
                role = MembershipRole.Customer;
                return true;
            default:
                role = default;
                return false;
        }
    }

    /// <exception cref="InvalidOperationException">The name is not a known role name.</exception>
    public static MembershipRole Parse(string name) =>
        TryParse(name, out var role) ? role : throw new InvalidOperationException("The stored membership role is not a known role name.");
}
