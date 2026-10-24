using TenantFlow.BuildingBlocks.Abstractions.Workflow;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.Payments.Contracts;
using TenantFlow.Subscriptions.Application.Abstractions;
using TenantFlow.Subscriptions.Domain.Subscriptions;
using TenantFlow.Tenants.Contracts;

namespace TenantFlow.Subscriptions.Application.Subscriptions.Services;

/// <summary>
/// The renewal job of one subscription (SPEC-0007 criteria 5 to 7, ADR-0023 decision 3), run by the Worker from the
/// subscription's due row in its tenant. Before charging it settles what is outstanding: the charge of the current attempt
/// (period + 1, failed attempts + 1), which Payments keeps at most one of. A subscription that ended while that charge was
/// in flight has it given back once it succeeds (review fix R1); one cancelled at its period end ends only when no charge
/// is outstanding. Renewals pause while the tenant is suspended (R11), stop for a removed member (R9), and after an
/// outage charge the current period only (R12). Every outcome is stored only if the subscription is still as it was read
/// (R2), so any run can repeat without a second charge or a second advance.
/// </summary>
public sealed class SubscriptionRenewer(
    ISubscriptionStore store,
    ISubscriptionCharges charges,
    IMembershipDirectory members,
    ITenantDirectory tenants,
    TimeProvider clock)
{
    public async Task<TimerDecision> RenewAsync(Guid subscriptionId, CancellationToken cancellationToken)
    {
        var subscription = await store.FindAsync(subscriptionId, cancellationToken);
        if (subscription is null)
        {
            return TimerDecision.Complete;
        }

        var now = clock.GetUtcNow();
        var (period, attempt) = (subscription.Period + 1, subscription.FailedAttempts + 1);
        if (subscription.Status == SubscriptionStatus.Cancelled)
        {
            return await SettleAfterEndAsync(subscription.Id, period, attempt, now, cancellationToken);
        }

        if (now < subscription.PeriodEnd)
        {
            return TimerDecision.RunAgain(subscription.PeriodEnd);
        }

        var availability = await tenants.GetAvailabilityAsync([subscription.TenantId], cancellationToken);
        if (availability.GetValueOrDefault(subscription.TenantId) != TenantAvailability.Active)
        {
            // Suspended (or gone): nothing is charged while the tenant cannot be used; renewals resume with it.
            return TimerDecision.RunAgain(now + SubscriptionRenewals.SuspendedCheckAfter);
        }

        var read = RenewalState.Of(subscription);
        if (!await members.IsActiveAsync(subscription.SubscriberMembershipId, cancellationToken))
        {
            // Ended, then run again at once: a charge still in flight is settled on the ended subscription's path.
            subscription.EndForRemovedMember(now);
            await store.SaveRenewalAsync(subscription, read, cancellationToken);
            return TimerDecision.RunAgain(now);
        }

        var outstanding = await charges.FindAsync(subscription.Id, period, attempt, cancellationToken);
        if (outstanding is null or { Status: SubscriptionChargeStatus.Failed } && subscription.EndIfCancelled(now))
        {
            return await store.SaveRenewalAsync(subscription, read, cancellationToken) ? TimerDecision.Complete : TimerDecision.RunAgain(now);
        }

        if (outstanding is null && subscription.SkipMissedPeriods(now))
        {
            // Stored before anything is charged, so the attempt asked for next is the same on every run.
            await store.SaveRenewalAsync(subscription, read, cancellationToken);
            return TimerDecision.RunAgain(now);
        }

        var charge = await charges.ChargeAsync(
            new SubscriptionCharge(subscription.Id, period, attempt, subscription.SubscriberMembershipId, subscription.Amount, subscription.Currency, subscription.PaymentMethod),
            cancellationToken);
        switch (charge.Status)
        {
            case SubscriptionChargeStatus.Succeeded:
                subscription.ChargeSucceeded(now);
                break;
            case SubscriptionChargeStatus.Failed:
                subscription.ChargeFailed(SubscriptionRenewals.MaxAttempts, now);
                break;
            case SubscriptionChargeStatus.InReview:
                // The period is not granted while the charge waits; it is asked about again, never charged again.
                if (subscription.ChargeInReview(now) && !await store.SaveRenewalAsync(subscription, read, cancellationToken))
                {
                    return TimerDecision.RunAgain(now);
                }

                return TimerDecision.RunAgain(now + SubscriptionRenewals.InReviewCheckAfter);
            default:
                // Not settled yet: Payments reconciles it; ask again, never charge again.
                return TimerDecision.RunAgain(now + SubscriptionRenewals.PendingCheckAfter);
        }

        if (!await store.SaveRenewalAsync(subscription, read, cancellationToken))
        {
            // Another change won (a cancel, a concurrent run): read the subscription again at once.
            return TimerDecision.RunAgain(now);
        }

        return subscription.Status switch
        {
            SubscriptionStatus.Cancelled => TimerDecision.Complete,
            SubscriptionStatus.PastDue => TimerDecision.RunAgain(now + SubscriptionRenewals.RetryAfter),
            _ => TimerDecision.RunAgain(subscription.PeriodEnd),
        };
    }

    // An ended subscription may still have a charge in flight: wait for it, and give it back if it succeeded.
    private async Task<TimerDecision> SettleAfterEndAsync(Guid subscriptionId, int period, int attempt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        switch ((await charges.FindAsync(subscriptionId, period, attempt, cancellationToken))?.Status)
        {
            case SubscriptionChargeStatus.Pending:
                return TimerDecision.RunAgain(now + SubscriptionRenewals.PendingCheckAfter);
            case SubscriptionChargeStatus.InReview:
                return TimerDecision.RunAgain(now + SubscriptionRenewals.InReviewCheckAfter);
            case SubscriptionChargeStatus.Succeeded:
                await charges.RefundAsync(subscriptionId, period, attempt, SubscriptionRenewals.AbandonedChargeReason, cancellationToken);
                return TimerDecision.Complete;
            default:
                return TimerDecision.Complete;
        }
    }
}
