using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.Payments.Contracts.Events;
using TenantFlow.Payments.Domain.Payments;
using TenantFlow.Payments.Domain.Refunds;

namespace TenantFlow.Payments.Application.Refunds.Services;

/// <summary>The event a refund publishes on reaching a final status (SPEC-0007 Contract); identifiers and amounts only.</summary>
public static class RefundEvents
{
    /// <summary>The event for the refund's current status, or null when that status announces nothing.</summary>
    public static OutgoingMessage? For(Refund refund, Payment payment)
    {
        ArgumentNullException.ThrowIfNull(refund);
        ArgumentNullException.ThrowIfNull(payment);
        if (refund.OrderId is not { } orderId)
        {
            // The refund of a subscription charge: no other module listens (review fix R1).
            return null;
        }

        var cancellation = refund.Kind == Refund.CancellationKind;
        return refund.Status switch
        {
            PaymentStatus.Succeeded => OutgoingMessage.From(new RefundSucceededV1(payment.Id, orderId, refund.Id, new PaymentAmount(refund.Amount, refund.Currency),
                payment.RefundedAmount, payment.RefundedAmount == payment.Amount, cancellation)),
            PaymentStatus.Failed => OutgoingMessage.From(new RefundFailedV1(payment.Id, orderId, refund.Id, cancellation, payment.RefundedAmount, refund.FailureCode)),
            _ => null,
        };
    }
}
