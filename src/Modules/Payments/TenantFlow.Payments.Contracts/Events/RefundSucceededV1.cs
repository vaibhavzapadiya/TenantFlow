using TenantFlow.BuildingBlocks.Abstractions.Messaging;

namespace TenantFlow.Payments.Contracts.Events;

/// <summary>
/// Published once when a refund of an order's payment succeeds (SPEC-0007 criterion 13). <see cref="RefundedTotal"/> is
/// what all succeeded refunds of the payment gave back so far; <see cref="FullyRefunded"/> is true once it equals the
/// captured amount. <see cref="Cancellation"/> marks the refund of a cancelled paid order.
/// </summary>
public sealed record RefundSucceededV1(Guid PaymentId, Guid OrderId, Guid RefundId, PaymentAmount Amount, decimal RefundedTotal, bool FullyRefunded, bool Cancellation) : IIntegrationEvent
{
    public static string EventType => "payment.refund_succeeded.v1";

    public static int EventVersion => 1;
}
