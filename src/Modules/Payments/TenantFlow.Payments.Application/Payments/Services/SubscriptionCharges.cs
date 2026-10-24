using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.Payments.Application.Abstractions;
using TenantFlow.Payments.Application.Refunds.Services;
using TenantFlow.Payments.Contracts;
using TenantFlow.Payments.Domain.Payments;

namespace TenantFlow.Payments.Application.Payments.Services;

/// <summary>
/// Charging a subscription period (ADR-0023 decision 3, SPEC-0007 criteria 5 to 7) for the Subscriptions module's renewal
/// job, in the timer's tenant. One payment per subscription, period and attempt, enforced by a unique index: a repeat finds
/// it and reports its status without calling the provider. A new payment and its reconciliation are committed first, then
/// the provider is called outside any transaction with the payment id as its idempotency key, then the outcome is
/// committed. A timeout is an unknown outcome, settled by reconciliation as for orders. No order event is published.
/// </summary>
public sealed class SubscriptionCharges(ITenantContext context, IPaymentStore store, PaymentProviders providers, RemainderRefunds remainder, TimeProvider clock) : ISubscriptionCharges
{
    public async Task<SubscriptionChargeResult?> FindAsync(Guid subscriptionId, int period, int attempt, CancellationToken cancellationToken) =>
        await store.FindChargeAsync(subscriptionId, period, attempt, cancellationToken) is { } payment
            ? Result(payment.Id, payment.Status, payment.FailureCode)
            : null;

    public async Task RefundAsync(Guid subscriptionId, int period, int attempt, string reason, CancellationToken cancellationToken)
    {
        if (await store.FindChargeAsync(subscriptionId, period, attempt, cancellationToken) is { } payment)
        {
            // Asked for by the subscriber's subscription ending: the subscriber is recorded as the requester (audit).
            await remainder.RefundAsync(payment, reason, payment.OwnerMembershipId, cancellationToken);
        }
    }

    public async Task<SubscriptionChargeResult> ChargeAsync(SubscriptionCharge charge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(charge);
        if (await store.FindChargeAsync(charge.SubscriptionId, charge.Period, charge.Attempt, cancellationToken) is { } existing)
        {
            return Result(existing.Id, existing.Status, existing.FailureCode);
        }

        var provider = providers.Current;
        var now = clock.GetUtcNow();
        var payment = Payment.StartCharge(Guid.CreateVersion7(now), context.TenantId, charge.SubscriptionId, charge.Period, charge.Attempt,
            charge.SubscriberMembershipId, charge.Amount, charge.Currency, provider.Name, charge.PaymentMethod, now);
        if (!await store.TryStartChargeAsync(payment, cancellationToken))
        {
            // A concurrent run won the attempt: report its payment, never send a second one.
            var winner = await store.FindChargeAsync(charge.SubscriptionId, charge.Period, charge.Attempt, cancellationToken)
                ?? throw new InvalidOperationException("The charge attempt is taken but its payment cannot be read.");
            return Result(winner.Id, winner.Status, winner.FailureCode);
        }

        // Committed: from here on finish regardless of the caller (the provider call has its own timeout).
        var committed = CancellationToken.None;
        var (settled, result) = await PaymentSender.SendAsync(store, provider, payment, committed);
        return Result(settled.Id, PaymentStatusNames.Parse(settled.Status), settled.Status == PaymentStatusNames.Failed ? result?.FailureCode : null);
    }

    private static SubscriptionChargeResult Result(Guid paymentId, PaymentStatus status, string? failureCode) => status switch
    {
        PaymentStatus.Succeeded => new(paymentId, SubscriptionChargeStatus.Succeeded, null),
        PaymentStatus.Failed => new(paymentId, SubscriptionChargeStatus.Failed, failureCode),
        PaymentStatus.RequiresReview => new(paymentId, SubscriptionChargeStatus.InReview, null),
        _ => new(paymentId, SubscriptionChargeStatus.Pending, null),
    };
}
