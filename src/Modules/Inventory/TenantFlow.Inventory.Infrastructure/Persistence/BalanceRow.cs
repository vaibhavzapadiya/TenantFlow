namespace TenantFlow.Inventory.Infrastructure.Persistence;

/// <summary>What a balance UPDATE returns (<c>RETURNING on_hand AS "OnHand", reserved AS "Reserved"</c>).</summary>
internal sealed class BalanceRow
{
    public long OnHand { get; init; }

    public long Reserved { get; init; }
}
