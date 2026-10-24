namespace TenantFlow.Tenants.Contracts;

/// <summary>
/// What other modules may ask the Tenants module (ADR-0002): whether tenants can be used right now. IdentityAccess
/// uses it to hide memberships of suspended tenants; it never reads the Tenants module's tables.
/// </summary>
public interface ITenantDirectory
{
    /// <summary>
    /// The availability of each tenant that exists. Ids that match no tenant are absent from the result, so a caller
    /// can tell "unknown" from "suspended" and still treat both as not usable.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, TenantAvailability>> GetAvailabilityAsync(
        IReadOnlyCollection<Guid> tenantIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Up to <paramref name="limit"/> tenant ids after <paramref name="after"/>, in id order, suspended tenants included:
    /// one page of every tenant, for work that runs per tenant such as retention (ADR-0025 decision 4).
    /// </summary>
    Task<IReadOnlyList<Guid>> ListIdsAsync(Guid? after, int limit, CancellationToken cancellationToken);
}
