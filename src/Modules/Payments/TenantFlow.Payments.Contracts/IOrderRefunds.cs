namespace TenantFlow.Payments.Contracts;

/// <summary>
/// The refund of a cancelled paid order (SPEC-0007 criterion 14, ADR-0023 decision 5), for the Orders module's
/// cancellation job, in the current tenant. One cancellation refund per payment, of what is left after the other refunds:
/// asking again never refunds again. The outcome arrives as <c>payment.refund_succeeded.v1</c> or
/// <c>payment.refund_failed.v1</c>.
/// </summary>
public interface IOrderRefunds
{
    /// <param name="orderId">The paid order being cancelled.</param>
    /// <param name="reason">The canceller's reason (audit).</param>
    /// <param name="requestedByMembershipId">Who cancelled (audit).</param>
    /// <param name="cancellationToken">Stops the request before the refund is stored.</param>
    Task<CancellationRefund> RefundRemainderAsync(Guid orderId, string reason, Guid requestedByMembershipId, CancellationToken cancellationToken);
}

/// <summary>What became of a cancellation refund request.</summary>
public enum CancellationRefund
{
    /// <summary>The refund is stored and sent (or was before); its event follows.</summary>
    Requested = 1,

    /// <summary>Nothing is left to refund: other refunds already gave everything back.</summary>
    NothingLeft = 2,

    /// <summary>The order has no succeeded payment to refund.</summary>
    NotRefundable = 3,

    /// <summary>Another refund of the order is not settled yet, so what is left is not known: ask again later (review fix R4).</summary>
    Waiting = 4,
}
