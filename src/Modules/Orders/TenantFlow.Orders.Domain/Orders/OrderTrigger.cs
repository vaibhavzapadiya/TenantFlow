namespace TenantFlow.Orders.Domain.Orders;

/// <summary>What can happen to an order. <see cref="OrderStateMachine"/> says in which state each is allowed.</summary>
public enum OrderTrigger
{
    /// <summary>The owner replaces the lines.</summary>
    EditLines = 1,

    /// <summary>The owner submits the order.</summary>
    Submit = 2,

    /// <summary>Inventory reserved every line.</summary>
    Reserved = 3,

    /// <summary>Inventory could not cover a line.</summary>
    ReservationFailed = 4,

    /// <summary>The owner or an administrator cancels the order.</summary>
    Cancel = 5,

    /// <summary>Payments reports the order's payment succeeded (<c>payment.succeeded.v1</c>).</summary>
    PaymentSucceeded = 6,

    /// <summary>Payments reports the order's payment failed (<c>payment.failed.v1</c>).</summary>
    PaymentFailed = 7,

    /// <summary>Payments gave up waiting for the payment's outcome (<c>payment.review_required.v1</c>).</summary>
    PaymentReviewRequired = 8,

    /// <summary>A refund succeeded and something is still captured (<c>payment.refund_succeeded.v1</c>, not fully refunded).</summary>
    RefundedPartly = 9,

    /// <summary>A refund succeeded and everything is given back (<c>payment.refund_succeeded.v1</c>, fully refunded).</summary>
    RefundedFully = 10,

    /// <summary>An <c>orders.manage</c> holder cancels a paid order: what is left is refunded.</summary>
    CancelPaid = 11,

    /// <summary>The cancellation refund failed and nothing was refunded before (<c>payment.refund_failed.v1</c>).</summary>
    CancellationRefundFailed = 12,

    /// <summary>The cancellation refund failed after other refunds gave part back (<c>payment.refund_failed.v1</c>).</summary>
    CancellationRefundFailedAfterRefunds = 13,

    /// <summary>A refund that is not a cancellation failed: the order does not move.</summary>
    RefundFailed = 14,
}
