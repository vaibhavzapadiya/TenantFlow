using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.Inventory.Contracts;
using TenantFlow.Orders.Application.Abstractions;
using TenantFlow.Orders.Application.Observability;
using TenantFlow.Orders.Contracts.Events;
using TenantFlow.Orders.Domain.Orders;

namespace TenantFlow.Orders.Application.Orders.Services;

/// <summary>
/// The second and third steps of a submit (ADR-0020): reserve every line in Inventory (its own transaction, idempotent by
/// order id), then move the order from <c>pending_reservation</c> to <c>pending_payment</c> or <c>rejected</c> with one
/// conditional update. Shared by the owner's submit and the Worker's recovery of an interrupted one, so both finish it the
/// same way and the reservation is never taken twice. A rejection is counted once, by the call that recorded it.
/// </summary>
public sealed class OrderReservationStep(IOrderStore store, IStockReservations reservations, OrdersMetrics metrics)
{
    public async Task FinishAsync(Order order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        var lines = order.Lines.Select(static l => new ReservationLine(l.ProductId, l.Quantity)).ToList();
        var outcome = await reservations.ReserveAsync(order.Id, lines, cancellationToken);
        var (trigger, reason) = outcome == ReservationOutcome.Reserved
            ? (OrderTrigger.Reserved, (string?)null)
            : (OrderTrigger.ReservationFailed, OrderRejectionReasons.InsufficientStock);
        var target = OrderStateMachine.Next(OrderStatus.PendingReservation, trigger) ?? throw new InvalidOperationException("The state table lost a reservation transition.");
        // A concurrent submit of the same order may finish first; the conditional update lets exactly one record it, and
        // only that one writes the event.
        var reserved = target == OrderStatus.PendingPayment
            ? OutgoingMessage.From(new OrderReservedV1(order.Id, order.CustomerMembershipId, new OrderTotal(order.TotalAmount, order.Currency), order.Lines.Count))
            : null;
        if (await store.TryTransitionAsync(order.Id, OrderStatus.PendingReservation, target, reason, null, reserved, cancellationToken) && reason is not null)
        {
            metrics.RecordReservationConflict(reason);
        }
    }
}
