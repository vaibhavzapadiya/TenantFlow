namespace TenantFlow.Inventory.Domain.Stock;

/// <summary>
/// The units of one product one order holds (SPEC-0004, ADR-0020). One row per (tenant, order, product), so reserving an
/// order twice finds the row instead of holding the units twice, and releasing knows exactly what to give back. Rows are
/// never deleted: a release marks the row <see cref="ReservationStatus.Released"/>. The store writes these rows with
/// conditional statements; the type exists for the mapping.
/// </summary>
public sealed class StockReservation
{
    private StockReservation()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid OrderId { get; private set; }

    public Guid ProductId { get; private set; }

    public long Quantity { get; private set; }

    public ReservationStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ReleasedAt { get; private set; }
}
