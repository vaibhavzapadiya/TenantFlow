using TenantFlow.BuildingBlocks.Abstractions.Workflow;
using TenantFlow.Orders.Application.Abstractions;
using TenantFlow.Orders.Domain.Orders;
using TenantFlow.Payments.Contracts;

namespace TenantFlow.Orders.Application.Orders.Services;

/// <summary>
/// The cancellation refund job (SPEC-0007 criterion 14, ADR-0023 decision 5): for an order in <c>cancellation_pending</c>, asks
/// Payments to refund what is left, through its contract, in the order's tenant. Payments keeps one cancellation refund per
/// payment, so a repeated run (a lost lease, a crash after the request) never refunds twice; its outcome arrives as an event.
/// When nothing is left to refund the order is refunded at once; while another refund is unsettled the job waits (review
/// fix R4); and an order whose payment cannot be refunded goes back to what it was (review fix R3).
/// </summary>
public sealed class OrderCancellationRefunds(IOrderStore store, IOrderRefunds refunds, TimeProvider clock)
{
    /// <summary>How long the job waits for another refund of the order to settle.</summary>
    public static readonly TimeSpan WaitForRefunds = TimeSpan.FromMinutes(1);

    public async Task<TimerDecision> RequestAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var record = await store.FindAsync(orderId, cancellationToken);
        if (record is not { Order: { Status: OrderStatus.CancellationPending } order })
        {
            return TimerDecision.Complete;
        }

        var outcome = await refunds.RefundRemainderAsync(orderId, order.CancellationReason ?? "Order cancelled", order.CancellationRequestedBy ?? order.CustomerMembershipId, cancellationToken);
        switch (outcome)
        {
            case CancellationRefund.NothingLeft:
                await store.TryTransitionAsync(orderId, OrderStatus.CancellationPending, order.NextOrThrow(OrderTrigger.RefundedFully), null, null, null, cancellationToken);
                break;
            case CancellationRefund.NotRefundable:
                await store.TryTransitionAsync(orderId, OrderStatus.CancellationPending, order.NextOrThrow(OrderTrigger.CancellationRefundFailed), null, null, null, cancellationToken);
                break;
            case CancellationRefund.Waiting:
                return TimerDecision.RunAgain(clock.GetUtcNow() + WaitForRefunds);
        }

        return TimerDecision.Complete;
    }
}
