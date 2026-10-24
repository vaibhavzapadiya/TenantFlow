using TenantFlow.Subscriptions.Domain.Plans;
using TenantFlow.Subscriptions.Domain.Subscriptions;

namespace TenantFlow.Subscriptions.Application.Abstractions;

/// <summary>
/// Storage for the Subscriptions use cases. Every method acts on the tenant in the current tenant context: it takes no
/// tenant id, filters by it, and runs in a transaction that sets it for row-level security (ADR-0011). With no tenant set
/// it throws before touching data.
/// </summary>
public interface ISubscriptionStore
{
    Task AddPlanAsync(Plan plan, CancellationToken cancellationToken);

    /// <summary>The tenant's active plans, oldest first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<Plan>> ListPlansAsync(int limit, CancellationToken cancellationToken);

    /// <summary>The plan, or null.</summary>
    Task<Plan?> FindPlanAsync(Guid planId, CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction: inserts the subscription and schedules its first charge (or trial end) at its period end
    /// (ADR-0023 decision 1). False, with nothing written, when the subscriber already has a live subscription to the plan.
    /// </summary>
    Task<bool> TryStartAsync(Subscription subscription, CancellationToken cancellationToken);

    /// <summary>The subscription, or null.</summary>
    Task<Subscription?> FindAsync(Guid subscriptionId, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a cancellation, only if the subscription is still in <paramref name="expected"/> with its cancel flag unset:
    /// false when another change won (ADR-0023 decision 2).
    /// </summary>
    Task<bool> SaveCancellationAsync(Subscription subscription, SubscriptionStatus expected, CancellationToken cancellationToken);

    /// <summary>
    /// Stores what a renewal did (status, period, failed attempts, cancel reason), only if the subscription is still in the
    /// state it was read in, its cancel flag included: false when another change won (a renewal, or a cancel at period end),
    /// so a repeated renewal advances a period at most once (SPEC-0007 criterion 6, review fix R2).
    /// </summary>
    Task<bool> SaveRenewalAsync(Subscription subscription, RenewalState expected, CancellationToken cancellationToken);
}
