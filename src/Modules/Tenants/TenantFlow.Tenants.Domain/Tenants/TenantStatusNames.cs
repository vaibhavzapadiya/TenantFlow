namespace TenantFlow.Tenants.Domain.Tenants;

/// <summary>
/// The stable names a <see cref="TenantStatus"/> has outside the domain: the database and the API both use these,
/// so the mapping exists once. Both directions are strict. A stored name nobody recognises is data corruption and
/// must fail loudly; it must never be read as some other status (a tenant silently suspended, or silently active).
/// </summary>
public static class TenantStatusNames
{
    public const string Active = "active";
    public const string Suspended = "suspended";

    /// <exception cref="InvalidOperationException">The status is not one the domain defines.</exception>
    public static string ToName(this TenantStatus status) => status switch
    {
        TenantStatus.Active => Active,
        TenantStatus.Suspended => Suspended,
        _ => throw new InvalidOperationException("The tenant status is not one the domain defines."),
    };

    /// <exception cref="InvalidOperationException">The name is not a known status name (case matters).</exception>
    public static TenantStatus Parse(string name) => name switch
    {
        Active => TenantStatus.Active,
        Suspended => TenantStatus.Suspended,
        _ => throw new InvalidOperationException("The stored tenant status is not a known status name."),
    };
}
