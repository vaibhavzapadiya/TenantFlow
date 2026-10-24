using TenantFlow.Orders.Domain.Orders;

namespace TenantFlow.Orders.Application.Orders.Services;

/// <summary>The order workflow's deadlines (SPEC-0006 criterion 13, ADR-0022 decision 7), kept as workflow timers.</summary>
public static class OrderDeadlines
{
    /// <summary>Timer kind: finish a submit that was interrupted between its committed steps.</summary>
    public const string ResumeSubmitKind = "orders.resume_submit";

    /// <summary>Timer kind: cancel a reserved order nobody started paying for.</summary>
    public const string ExpireUnpaidKind = "orders.expire_unpaid";

    /// <summary>Timer kind: ask Payments for the refund of a cancelled paid order (SPEC-0007 criterion 14).</summary>
    public const string RequestCancellationRefundKind = "orders.request_cancellation_refund";

    /// <summary>How long a submit may stay in <c>pending_reservation</c> before the Worker finishes it.</summary>
    public static readonly TimeSpan ResumeSubmitAfter = TimeSpan.FromMinutes(5);

    /// <summary>How long a reserved order waits for its payment to begin before it is cancelled and its stock released.</summary>
    public static readonly TimeSpan PaymentWindow = TimeSpan.FromMinutes(30);

    /// <summary>The timer a move to <paramref name="target"/> schedules, or null.</summary>
    public static (string Kind, TimeSpan After)? For(OrderStatus target) => target switch
    {
        OrderStatus.PendingReservation => (ResumeSubmitKind, ResumeSubmitAfter),
        OrderStatus.PendingPayment => (ExpireUnpaidKind, PaymentWindow),
        OrderStatus.CancellationPending => (RequestCancellationRefundKind, TimeSpan.Zero),
        _ => null,
    };
}
