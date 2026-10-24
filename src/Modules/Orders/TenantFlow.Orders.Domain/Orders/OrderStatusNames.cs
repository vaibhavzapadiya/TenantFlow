namespace TenantFlow.Orders.Domain.Orders;

/// <summary>
/// The stable names an <see cref="OrderStatus"/> has outside the domain: the database and the API both use these, so the
/// mapping exists once. A stored name nobody recognises is data corruption and fails loudly.
/// </summary>
public static class OrderStatusNames
{
    public const string Draft = "draft";
    public const string PendingReservation = "pending_reservation";
    public const string PendingPayment = "pending_payment";
    public const string Rejected = "rejected";
    public const string Cancelled = "cancelled";
    public const string Paid = "paid";
    public const string PaymentFailed = "payment_failed";
    public const string PaymentReview = "payment_review";
    public const string PartiallyRefunded = "partially_refunded";
    public const string CancellationPending = "cancellation_pending";
    public const string Refunded = "refunded";

    /// <summary>Every name, for the database CHECK constraint.</summary>
    public static readonly IReadOnlyList<string> All = [Draft, PendingReservation, PendingPayment, Rejected, Cancelled, Paid, PaymentFailed, PaymentReview, PartiallyRefunded, CancellationPending, Refunded];

    /// <exception cref="InvalidOperationException">The status is not one the domain defines.</exception>
    public static string ToName(this OrderStatus status) => status switch
    {
        OrderStatus.Draft => Draft,
        OrderStatus.PendingReservation => PendingReservation,
        OrderStatus.PendingPayment => PendingPayment,
        OrderStatus.Rejected => Rejected,
        OrderStatus.Cancelled => Cancelled,
        OrderStatus.Paid => Paid,
        OrderStatus.PaymentFailed => PaymentFailed,
        OrderStatus.PaymentReview => PaymentReview,
        OrderStatus.PartiallyRefunded => PartiallyRefunded,
        OrderStatus.CancellationPending => CancellationPending,
        OrderStatus.Refunded => Refunded,
        _ => throw new InvalidOperationException("The order status is not one the domain defines."),
    };

    /// <exception cref="InvalidOperationException">The stored name is not a known status name (case matters).</exception>
    public static OrderStatus Parse(string name) => name switch
    {
        Draft => OrderStatus.Draft,
        PendingReservation => OrderStatus.PendingReservation,
        PendingPayment => OrderStatus.PendingPayment,
        Rejected => OrderStatus.Rejected,
        Cancelled => OrderStatus.Cancelled,
        Paid => OrderStatus.Paid,
        PaymentFailed => OrderStatus.PaymentFailed,
        PaymentReview => OrderStatus.PaymentReview,
        PartiallyRefunded => OrderStatus.PartiallyRefunded,
        CancellationPending => OrderStatus.CancellationPending,
        Refunded => OrderStatus.Refunded,
        _ => throw new InvalidOperationException("The stored order status is not a known status name."),
    };
}
