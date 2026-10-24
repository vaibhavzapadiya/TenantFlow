using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.Payments.Contracts.Events;
using TenantFlow.Payments.Domain.Payments;

namespace TenantFlow.Payments.Application.Payments.Services;

/// <summary>The event a payment publishes on reaching a status (SPEC-0006 Contract); identifiers and amounts only.</summary>
public static class PaymentEvents
{
    /// <summary>The event for the payment's current status, or null when that status announces nothing.</summary>
    public static OutgoingMessage? For(Payment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);
        if (payment.OrderId is not { } orderId)
        {
            // A subscription charge: its renewal job asks for the outcome; no other module listens (ADR-0023 decision 3).
            return null;
        }

        var amount = new PaymentAmount(payment.Amount, payment.Currency);
        return payment.Status switch
        {
            PaymentStatus.Succeeded => OutgoingMessage.From(new PaymentSucceededV1(payment.Id, orderId, amount)),
            PaymentStatus.Failed => OutgoingMessage.From(new PaymentFailedV1(payment.Id, orderId, amount, payment.FailureCode)),
            PaymentStatus.RequiresReview => OutgoingMessage.From(new PaymentReviewRequiredV1(payment.Id, orderId, amount)),
            _ => null,
        };
    }
}
