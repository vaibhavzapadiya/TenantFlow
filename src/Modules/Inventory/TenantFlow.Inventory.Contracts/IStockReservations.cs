namespace TenantFlow.Inventory.Contracts;

/// <summary>
/// What other modules may ask the Inventory module about stock (ADR-0002, ADR-0020). Orders reserves an order's stock on
/// submit and releases it on cancel. Each call runs in Inventory's own transaction for the tenant in the current tenant
/// context, and both are idempotent by order id, so a caller recovers from an interruption by repeating the call.
/// </summary>
public interface IStockReservations
{
    /// <summary>
    /// Reserves every line or none. Calling it again with the same lines for an order that still holds its reservation
    /// changes nothing and answers <see cref="ReservationOutcome.Reserved"/>. The lines must be at least one, each product
    /// once, each quantity above zero.
    /// </summary>
    /// <exception cref="ArgumentException">The lines break those rules: a programming error in the caller.</exception>
    /// <exception cref="InvalidOperationException">
    /// The order's reservation was released, or was taken for other lines: an order is reserved once, for fixed lines.
    /// </exception>
    Task<ReservationOutcome> ReserveAsync(Guid orderId, IReadOnlyCollection<ReservationLine> lines, CancellationToken cancellationToken);

    /// <summary>Gives back what the order holds. Releasing an order that holds nothing, or again, changes nothing.</summary>
    Task ReleaseAsync(Guid orderId, CancellationToken cancellationToken);
}
