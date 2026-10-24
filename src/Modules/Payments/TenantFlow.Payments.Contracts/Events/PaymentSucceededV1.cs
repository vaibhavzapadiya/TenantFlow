using TenantFlow.BuildingBlocks.Abstractions.Messaging;

namespace TenantFlow.Payments.Contracts.Events;

/// <summary>Published once when a payment moves to <c>succeeded</c> (SPEC-0006 criteria 1, 7 and 11).</summary>
public sealed record PaymentSucceededV1(Guid PaymentId, Guid OrderId, PaymentAmount Amount) : IIntegrationEvent
{
    public static string EventType => "payment.succeeded.v1";

    public static int EventVersion => 1;
}
