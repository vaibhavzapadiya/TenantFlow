namespace TenantFlow.Orders.Domain.Orders;

/// <summary>
/// The order's state table (SPEC-0004 criterion 11, ADR-0020; payment states SPEC-0006 criterion 11, ADR-0022; refund states
/// SPEC-0007 criteria 13 and 14, ADR-0023 decision 5): the one place that says which trigger is allowed in
/// which state and where it leads. Anything not listed is an illegal transition. The store applies a transition with a
/// conditional update on the expected state, so two racing commands cannot both win.
/// </summary>
public static class OrderStateMachine
{
    private static readonly Dictionary<(OrderStatus From, OrderTrigger Trigger), OrderStatus> Transitions = new()
    {
        [(OrderStatus.Draft, OrderTrigger.EditLines)] = OrderStatus.Draft,
        [(OrderStatus.Draft, OrderTrigger.Submit)] = OrderStatus.PendingReservation,
        [(OrderStatus.PendingReservation, OrderTrigger.Reserved)] = OrderStatus.PendingPayment,
        [(OrderStatus.PendingReservation, OrderTrigger.ReservationFailed)] = OrderStatus.Rejected,
        [(OrderStatus.Draft, OrderTrigger.Cancel)] = OrderStatus.Cancelled,
        [(OrderStatus.PendingPayment, OrderTrigger.Cancel)] = OrderStatus.Cancelled,
        [(OrderStatus.PendingPayment, OrderTrigger.PaymentSucceeded)] = OrderStatus.Paid,
        [(OrderStatus.PendingPayment, OrderTrigger.PaymentFailed)] = OrderStatus.PaymentFailed,
        [(OrderStatus.PendingPayment, OrderTrigger.PaymentReviewRequired)] = OrderStatus.PaymentReview,
        [(OrderStatus.PaymentReview, OrderTrigger.PaymentSucceeded)] = OrderStatus.Paid,
        [(OrderStatus.PaymentReview, OrderTrigger.PaymentFailed)] = OrderStatus.PaymentFailed,
        [(OrderStatus.Paid, OrderTrigger.RefundedPartly)] = OrderStatus.PartiallyRefunded,
        [(OrderStatus.PartiallyRefunded, OrderTrigger.RefundedPartly)] = OrderStatus.PartiallyRefunded,
        [(OrderStatus.Paid, OrderTrigger.RefundedFully)] = OrderStatus.Refunded,
        [(OrderStatus.PartiallyRefunded, OrderTrigger.RefundedFully)] = OrderStatus.Refunded,
        [(OrderStatus.Paid, OrderTrigger.CancelPaid)] = OrderStatus.CancellationPending,
        [(OrderStatus.PartiallyRefunded, OrderTrigger.CancelPaid)] = OrderStatus.CancellationPending,
        // An earlier partial refund may still finish while the cancellation runs; the order stays in cancellation.
        [(OrderStatus.CancellationPending, OrderTrigger.RefundedPartly)] = OrderStatus.CancellationPending,
        [(OrderStatus.CancellationPending, OrderTrigger.RefundedFully)] = OrderStatus.Refunded,
        [(OrderStatus.CancellationPending, OrderTrigger.CancellationRefundFailed)] = OrderStatus.Paid,
        [(OrderStatus.CancellationPending, OrderTrigger.CancellationRefundFailedAfterRefunds)] = OrderStatus.PartiallyRefunded,
    };

    /// <summary>Every allowed transition, for tests and documentation.</summary>
    public static IEnumerable<(OrderStatus From, OrderTrigger Trigger, OrderStatus To)> All =>
        Transitions.Select(static t => (t.Key.From, t.Key.Trigger, t.Value));

    /// <summary>The state <paramref name="trigger"/> leads to from <paramref name="from"/>, or null when it is not allowed.</summary>
    public static OrderStatus? Next(OrderStatus from, OrderTrigger trigger) =>
        Transitions.TryGetValue((from, trigger), out var to) ? to : null;
}
