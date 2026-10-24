using TenantFlow.Payments.Application.Abstractions;
using TenantFlow.Payments.Domain.Payments;
using TenantFlow.Payments.Domain.Refunds;

namespace TenantFlow.Payments.Application.Refunds.Services;

/// <summary>What became of a request to refund what is left of a payment.</summary>
public enum RemainderRefund
{
    /// <summary>The refund is stored and sent (or was before); its outcome follows.</summary>
    Requested = 1,

    /// <summary>Nothing is left: other refunds gave everything back.</summary>
    NothingLeft = 2,

    /// <summary>The payment has not succeeded.</summary>
    NotRefundable = 3,

    /// <summary>Another refund is not settled yet; ask again later.</summary>
    Waiting = 4,
}

/// <summary>
/// The refund of what is left of a payment (a cancelled paid order, SPEC-0007 criterion 14, or a subscription charge whose
/// subscription ended before it settled, review fix R1). One per payment that has not failed: a repeat re-sends it only
/// while its outcome is not known, with the same provider key, and a failed one is followed by a new one.
/// </summary>
public sealed class RemainderRefunds(IPaymentStore store, RefundSender sender, TimeProvider clock)
{
    public async Task<RemainderRefund> RefundAsync(Payment payment, string reason, Guid requestedByMembershipId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payment);
        if (payment.Status != PaymentStatus.Succeeded)
        {
            return RemainderRefund.NotRefundable;
        }

        if (await store.FindCancellationRefundAsync(payment.Id, cancellationToken) is { } existing)
        {
            if (existing.Status == PaymentStatus.Processing)
            {
                await sender.SendAsync(existing, payment, CancellationToken.None);
            }

            return RemainderRefund.Requested;
        }

        var now = clock.GetUtcNow();
        // The amount is a placeholder the store replaces with what is left, under the payment's lock.
        var refund = Refund.Start(Guid.CreateVersion7(now), payment, payment.Amount, Refund.CancellationKind, reason, requestedByMembershipId, now);
        // Not audited itself: it follows the paid order cancellation, which is (SPEC-0009 criterion 2).
        switch (await store.TryStartRefundAsync(refund, null, null, null, cancellationToken))
        {
            case RefundStart.Started:
                var started = await store.FindRefundAsync(refund.Id, cancellationToken)
                    ?? throw new InvalidOperationException("The remainder refund was stored but cannot be read.");
                await sender.SendAsync(started, payment, CancellationToken.None);
                return RemainderRefund.Requested;
            case RefundStart.ExceedsCaptured:
                return RemainderRefund.NothingLeft;
            case RefundStart.CancellationExists:
                return RemainderRefund.Requested;
            case RefundStart.RefundsUnsettled:
                return RemainderRefund.Waiting;
            default:
                return RemainderRefund.NotRefundable;
        }
    }
}
