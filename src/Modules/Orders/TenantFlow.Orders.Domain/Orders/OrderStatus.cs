namespace TenantFlow.Orders.Domain.Orders;

/// <summary>Where an order is in its lifecycle (SPEC-0004, ADR-0020; payment states SPEC-0006, ADR-0022).</summary>
public enum OrderStatus
{
    /// <summary>Being put together by its owner; lines can change.</summary>
    Draft = 1,

    /// <summary>Submitted; its stock is being reserved. An interrupted submit leaves the order here until submitted again.</summary>
    PendingReservation = 2,

    /// <summary>Its stock is reserved; it waits for payment.</summary>
    PendingPayment = 3,

    /// <summary>The stock could not cover it. Final.</summary>
    Rejected = 4,

    /// <summary>Cancelled by its owner or an administrator; any reservation is released. Final.</summary>
    Cancelled = 5,

    /// <summary>Its payment succeeded. Fulfilment states come later.</summary>
    Paid = 6,

    /// <summary>Its payment definitively failed; its reservation is released. Final.</summary>
    PaymentFailed = 7,

    /// <summary>Its payment's outcome stayed unknown after reconciliation; a late answer still settles it.</summary>
    PaymentReview = 8,

    /// <summary>Paid, and refunds gave part of it back (SPEC-0007 criterion 13).</summary>
    PartiallyRefunded = 9,

    /// <summary>A paid order being cancelled: the refund of what is left is requested (SPEC-0007 criterion 14).</summary>
    CancellationPending = 10,

    /// <summary>Refunds gave everything back. Final.</summary>
    Refunded = 11,
}
