using Microsoft.EntityFrameworkCore;
using TenantFlow.BuildingBlocks.Persistence;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.Inventory.Application.Abstractions;
using TenantFlow.Inventory.Domain.Stock;

namespace TenantFlow.Inventory.Infrastructure.Persistence;

/// <summary>
/// Every operation filters by the tenant context's id and runs in a transaction that sets it for row-level security
/// (ADR-0011). Stock never goes below zero, nor below what orders have reserved (SPEC-0004): the change is one
/// conditional UPDATE, so concurrent adjustments and reservations queue on the row lock and each re-checks the condition
/// against the committed balance (no read-then-write), and CHECK constraints back it up.
/// </summary>
internal sealed class InventoryStore(InventoryDbContext db, ITenantContext tenant) : IInventoryStore
{
    public async Task<StockLevel> GetLevelAsync(Guid productId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        return await db.Balances.AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.ProductId == productId)
            .Select(b => (StockLevel?)new StockLevel(b.OnHand, b.Reserved))
            .SingleOrDefaultAsync(cancellationToken) ?? StockLevel.None;
    }

    public async Task<StockLevel?> TryApplyAsync(StockAdjustment adjustment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adjustment);
        var tenantId = tenant.TenantId;
        if (adjustment.TenantId != tenantId)
        {
            // A programming error: an adjustment is recorded for the caller's tenant only.
            throw new InvalidOperationException("A stock adjustment can only be applied to the tenant in the current context.");
        }

        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        // The first adjustment of a product creates its balance at zero; a concurrent first adjustment simply finds it.
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO inventory.stock_balances (tenant_id, product_id, on_hand, updated_at)
            VALUES ({tenantId}, {adjustment.ProductId}, 0, {adjustment.OccurredAt})
            ON CONFLICT (tenant_id, product_id) DO NOTHING
            """,
            cancellationToken);
        // reserved >= 0 always, so the condition also keeps on_hand at zero or above.
        var updated = await db.Database.SqlQuery<BalanceRow>(
            $"""
            UPDATE inventory.stock_balances
            SET on_hand = on_hand + {adjustment.Delta}, updated_at = {adjustment.OccurredAt}
            WHERE tenant_id = {tenantId} AND product_id = {adjustment.ProductId} AND on_hand + {adjustment.Delta} >= reserved
            RETURNING on_hand AS "OnHand", reserved AS "Reserved"
            """).ToListAsync(cancellationToken);
        if (updated.Count == 0)
        {
            // Insufficient stock: roll back, so not even the zero balance row of a first adjustment remains.
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        db.Adjustments.Add(adjustment);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new StockLevel(updated[0].OnHand, updated[0].Reserved);
    }
}
