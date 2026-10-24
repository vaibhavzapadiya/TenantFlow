namespace TenantFlow.Inventory.Contracts;

/// <summary>How many units of one product an order asks to reserve.</summary>
public sealed record ReservationLine(Guid ProductId, int Quantity);
