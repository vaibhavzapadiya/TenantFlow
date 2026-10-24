namespace TenantFlow.Inventory.Contracts;

/// <summary>The answer to a reservation: a business outcome, not an error.</summary>
public enum ReservationOutcome
{
    /// <summary>Every line is reserved for the order.</summary>
    Reserved = 1,

    /// <summary>At least one line exceeded the available stock; nothing was reserved.</summary>
    InsufficientStock = 2,
}
