using TenantFlow.Subscriptions.Domain.Plans;
using TenantFlow.Subscriptions.Domain.Subscriptions;

namespace TenantFlow.Subscriptions.Application.Subscriptions.Dtos;

/// <summary>A subscription as callers see it. The payment-method token is not shown.</summary>
public sealed record SubscriptionDetails(
    Guid Id,
    Guid PlanId,
    string Status,
    decimal Amount,
    string Currency,
    string Interval,
    DateTimeOffset CurrentPeriodEnd,
    bool CancelAtPeriodEnd,
    DateTimeOffset CreatedAt)
{
    public static SubscriptionDetails From(Subscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        return new SubscriptionDetails(subscription.Id, subscription.PlanId, subscription.Status.ToName(), subscription.Amount, subscription.Currency,
            subscription.Interval.ToName(), subscription.PeriodEnd, subscription.CancelAtPeriodEnd, subscription.CreatedAt);
    }
}
