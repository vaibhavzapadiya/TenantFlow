namespace TenantFlow.Inventory.Infrastructure.Persistence;

/// <summary>One reserved line an order still holds, as the release query returns it.</summary>
internal sealed class HeldReservationRow
{
    public Guid ProductId { get; init; }

    public long Quantity { get; init; }
}
