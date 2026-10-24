using TenantFlow.BuildingBlocks.Abstractions.Messaging;

namespace TenantFlow.Payments.Contracts.Events;

/// <summary>Published once when reconciliation gives up waiting and a payment moves to <c>requires_review</c> (SPEC-0006 criterion 12).</summary>
public sealed record PaymentReviewRequiredV1(Guid PaymentId, Guid OrderId, PaymentAmount Amount) : IIntegrationEvent
{
    public static string EventType => "payment.review_required.v1";

    public static int EventVersion => 1;
}
