using TenantFlow.BuildingBlocks.Abstractions.Messaging;

namespace TenantFlow.Payments.Contracts.Events;

/// <summary>
/// Published once when a refund of an order's payment definitively fails (SPEC-0007 criterion 14). <see cref="RefundedTotal"/>
/// is what succeeded refunds of the payment gave back so far.
/// </summary>
public sealed record RefundFailedV1(Guid PaymentId, Guid OrderId, Guid RefundId, bool Cancellation, decimal RefundedTotal, string? FailureCode) : IIntegrationEvent
{
    public static string EventType => "payment.refund_failed.v1";

    public static int EventVersion => 1;
}
