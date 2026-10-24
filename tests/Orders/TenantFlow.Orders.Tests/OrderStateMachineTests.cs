using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.Orders.Domain.Orders;

namespace TenantFlow.Orders.Tests;

/// <summary>SPEC-0004 criterion 11, SPEC-0006 criterion 11 and SPEC-0007 criteria 13 and 14 (ADR-0020, ADR-0022, ADR-0023): the state table, whole, and an illegal edit refused by the aggregate.</summary>
public sealed class OrderStateMachineTests
{
    [Fact]
    public void Exactly_the_reviewed_transitions_are_allowed_and_every_other_pair_is_refused()
    {
        var allowed = new HashSet<(OrderStatus, OrderTrigger, OrderStatus)>
        {
            (OrderStatus.Draft, OrderTrigger.EditLines, OrderStatus.Draft),
            (OrderStatus.Draft, OrderTrigger.Submit, OrderStatus.PendingReservation),
            (OrderStatus.PendingReservation, OrderTrigger.Reserved, OrderStatus.PendingPayment),
            (OrderStatus.PendingReservation, OrderTrigger.ReservationFailed, OrderStatus.Rejected),
            (OrderStatus.Draft, OrderTrigger.Cancel, OrderStatus.Cancelled),
            (OrderStatus.PendingPayment, OrderTrigger.Cancel, OrderStatus.Cancelled),
            (OrderStatus.PendingPayment, OrderTrigger.PaymentSucceeded, OrderStatus.Paid),
            (OrderStatus.PendingPayment, OrderTrigger.PaymentFailed, OrderStatus.PaymentFailed),
            (OrderStatus.PendingPayment, OrderTrigger.PaymentReviewRequired, OrderStatus.PaymentReview),
            (OrderStatus.PaymentReview, OrderTrigger.PaymentSucceeded, OrderStatus.Paid),
            (OrderStatus.PaymentReview, OrderTrigger.PaymentFailed, OrderStatus.PaymentFailed),
            (OrderStatus.Paid, OrderTrigger.RefundedPartly, OrderStatus.PartiallyRefunded),
            (OrderStatus.PartiallyRefunded, OrderTrigger.RefundedPartly, OrderStatus.PartiallyRefunded),
            (OrderStatus.Paid, OrderTrigger.RefundedFully, OrderStatus.Refunded),
            (OrderStatus.PartiallyRefunded, OrderTrigger.RefundedFully, OrderStatus.Refunded),
            (OrderStatus.Paid, OrderTrigger.CancelPaid, OrderStatus.CancellationPending),
            (OrderStatus.PartiallyRefunded, OrderTrigger.CancelPaid, OrderStatus.CancellationPending),
            (OrderStatus.CancellationPending, OrderTrigger.RefundedPartly, OrderStatus.CancellationPending),
            (OrderStatus.CancellationPending, OrderTrigger.RefundedFully, OrderStatus.Refunded),
            (OrderStatus.CancellationPending, OrderTrigger.CancellationRefundFailed, OrderStatus.Paid),
            (OrderStatus.CancellationPending, OrderTrigger.CancellationRefundFailedAfterRefunds, OrderStatus.PartiallyRefunded),
        };

        var actual = Enum.GetValues<OrderStatus>()
            .SelectMany(from => Enum.GetValues<OrderTrigger>().Select(trigger => (from, trigger, to: OrderStateMachine.Next(from, trigger))))
            .Where(static t => t.to is not null)
            .Select(static t => (t.from, t.trigger, t.to!.Value))
            .ToHashSet();

        Assert.Equal(allowed, actual);
    }

    [Fact]
    public void Final_states_allow_nothing()
    {
        foreach (var final in new[] { OrderStatus.Rejected, OrderStatus.Cancelled, OrderStatus.PaymentFailed, OrderStatus.Refunded })
        {
            Assert.All(Enum.GetValues<OrderTrigger>(), trigger => Assert.Null(OrderStateMachine.Next(final, trigger)));
        }
    }

    [Fact]
    public void An_order_refuses_a_trigger_its_state_does_not_allow()
    {
        var order = OrderRuleTests.Draft();

        var refused = Assert.Throws<AppException>(() => order.NextOrThrow(OrderTrigger.Reserved));

        Assert.Equal(OrderErrors.InvalidTransition, refused.Error);
        Assert.Equal(OrderStatus.PendingReservation, order.NextOrThrow(OrderTrigger.Submit));
    }
}
