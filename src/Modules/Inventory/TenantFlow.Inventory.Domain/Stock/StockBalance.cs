namespace TenantFlow.Inventory.Domain.Stock;

/// <summary>
/// How many units of one product one tenant has on hand (SPEC-0003 criteria 11 to 13). It is never changed by
/// read-then-write: the store applies an adjustment with one conditional update, so the database keeps
/// <see cref="OnHand"/> at zero or above under any concurrency. A product with no row has zero on hand.
/// </summary>
public sealed class StockBalance
{
    private StockBalance()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid ProductId { get; private set; }

    /// <summary>A 64-bit count: repeated maximum adjustments cannot overflow it.</summary>
    public long OnHand { get; private set; }

    /// <summary>
    /// Units promised to orders (SPEC-0004). Always between zero and <see cref="OnHand"/>: reservations and adjustments
    /// change it only with conditional updates, and a CHECK constraint backs them up.
    /// </summary>
    public long Reserved { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
}
