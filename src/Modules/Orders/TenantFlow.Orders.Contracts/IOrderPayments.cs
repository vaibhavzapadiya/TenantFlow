namespace TenantFlow.Orders.Contracts;

/// <summary>
/// What the Payments module may ask the Orders module (ADR-0002, ADR-0022). It acts on the tenant and membership in the
/// current tenant context, in Orders' own transaction.
/// </summary>
public interface IOrderPayments
{
    /// <summary>
    /// What paying the caller's own <c>pending_payment</c> order would charge. Reads only: nothing is marked, so a request
    /// that stops here leaves the order as it was. An order whose payment has already begun is still payable here (a
    /// repeated request); Payments then finds its existing payment.
    /// </summary>
    /// <exception cref="TenantFlow.BuildingBlocks.Abstractions.Errors.AppException">
    /// <c>orders.order_not_found</c> (unknown, another tenant's or another customer's order), or
    /// <c>orders.invalid_transition</c> (the order is not waiting for payment).
    /// </exception>
    Task<PayableOrder> GetPayableAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the payment begun, with one conditional update, once Payments has committed the payment: from then on the
    /// order cannot be cancelled or expire. True when the order is marked (now or by an earlier request); false when it is
    /// no longer waiting for payment (cancelled or expired meanwhile), in which case nothing may be charged.
    /// </summary>
    Task<bool> BeginPaymentAsync(Guid orderId, CancellationToken cancellationToken);
}
