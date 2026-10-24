using TenantFlow.Subscriptions.Domain.Subscriptions;

namespace TenantFlow.Subscriptions.Application.Abstractions;

/// <summary>What a renewal read: the subscription must still be exactly this for the renewal's outcome to be stored.</summary>
public sealed record RenewalState(SubscriptionStatus Status, int Period, int FailedAttempts, bool CancelAtPeriodEnd)
{
    public static RenewalState Of(Subscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        return new(subscription.Status, subscription.Period, subscription.FailedAttempts, subscription.CancelAtPeriodEnd);
    }
}
