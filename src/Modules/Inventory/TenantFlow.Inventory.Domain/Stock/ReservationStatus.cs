namespace TenantFlow.Inventory.Domain.Stock;

/// <summary>Whether a reservation still holds its units. Stored as text; the names are part of the schema.</summary>
public enum ReservationStatus
{
    Reserved = 1,
    Released = 2,
}
