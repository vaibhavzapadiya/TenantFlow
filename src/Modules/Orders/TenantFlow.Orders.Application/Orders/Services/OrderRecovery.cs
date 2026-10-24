using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.BuildingBlocks.Abstractions.Workflow;
using TenantFlow.Inventory.Contracts;
using TenantFlow.Orders.Application.Abstractions;
using TenantFlow.Orders.Contracts.Events;
using TenantFlow.Orders.Domain.Orders;

namespace TenantFlow.Orders.Application.Orders.Services;

/// <summary>
/// The order workflow's deadlines (SPEC-0006 criterion 13): the Worker runs them in the order's tenant from a timer, with
/// no user. Each checks the order as it is now, so a deadline that is no longer needed just ends, and each is idempotent:
/// a crash or a lost lease runs it again with the same result.
/// </summary>
public sealed class OrderRecovery(IOrderStore store, OrderReservationStep reservation, IStockReservations reservations)
{
    /// <summary>Finishes a submit still in <c>pending_reservation</c> (reserve, then <c>pending_payment</c> or <c>rejected</c>).</summary>
    public async Task<TimerDecision> ResumeSubmitAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var record = await store.FindAsync(orderId, cancellationToken);
        if (record?.Order.Status == OrderStatus.PendingReservation)
        {
            await reservation.FinishAsync(record.Order, cancellationToken);
        }

        return TimerDecision.Complete;
    }

    /// <summary>
    /// Cancels a reserved order whose payment never began and gives its stock back. A begun payment is never expired: the
    /// store's conditional update refuses the cancel once <c>payment_started_at</c> is set, whoever wins the race.
    /// </summary>
    public async Task<TimerDecision> ExpireUnpaidAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = (await store.FindAsync(orderId, cancellationToken))?.Order;
        if (order is { Status: OrderStatus.PendingPayment, PaymentStartedAt: null })
        {
            var cancelled = OutgoingMessage.From(new OrderCancelledV1(orderId, order.CustomerMembershipId, order.Status.ToName()));
            if (await store.TryTransitionAsync(orderId, OrderStatus.PendingPayment, order.NextOrThrow(OrderTrigger.Cancel), null, null, cancelled, cancellationToken))
            {
                order = (await store.FindAsync(orderId, cancellationToken))?.Order;
            }
        }

        // Cancelled now or by an earlier run that crashed before the release: give the units back (idempotent).
        if (order?.Status == OrderStatus.Cancelled)
        {
            await reservations.ReleaseAsync(orderId, cancellationToken);
        }

        return TimerDecision.Complete;
    }
}
