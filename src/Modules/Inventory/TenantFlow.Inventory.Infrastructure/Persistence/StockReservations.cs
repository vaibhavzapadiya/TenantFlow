using Microsoft.EntityFrameworkCore;
using TenantFlow.BuildingBlocks.Persistence;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.Inventory.Contracts;
using TenantFlow.Inventory.Domain.Stock;
using TenantFlow.Inventory.Infrastructure.Persistence.Configurations;

namespace TenantFlow.Inventory.Infrastructure.Persistence;

/// <summary>
/// Reserves and releases an order's stock (SPEC-0004 criteria 1 to 5, ADR-0020), in one tenant-scoped transaction per
/// call (ADR-0011).
/// <list type="bullet">
/// <item>No oversell: each line is one conditional UPDATE (<c>on_hand - reserved &gt;= quantity</c>), so concurrent orders
/// queue on the balance row and each re-checks against the committed value; a CHECK constraint backs it up. One line that
/// does not fit rolls the whole transaction back.</item>
/// <item>Idempotent: the reservation row's key is (tenant, order, product), inserted before the balance changes. A repeat,
/// even a concurrent one, waits on that key, finds a row, rolls back without touching any balance, and is answered from
/// the committed rows: still held for exactly these lines means reserved; anything else (released, or rows for other
/// lines) is a caller error, never a silent "reserved". Release flips rows from reserved to released, so a second
/// release finds nothing to give back.</item>
/// <item>Deadlock-free between orders: lines are taken in product-id order.</item>
/// </list>
/// </summary>
internal sealed class StockReservations(InventoryDbContext db, ITenantContext tenant, TimeProvider clock) : IStockReservations
{
    public async Task<ReservationOutcome> ReserveAsync(Guid orderId, IReadOnlyCollection<ReservationLine> lines, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (orderId == Guid.Empty || lines.Count == 0 || lines.Any(static l => l.ProductId == Guid.Empty || l.Quantity <= 0)
            || lines.Select(static l => l.ProductId).Distinct().Count() != lines.Count)
        {
            throw new ArgumentException("A reservation needs an order and at least one line, each product once, each quantity above zero.", nameof(lines));
        }

        var tenantId = tenant.TenantId;
        var now = clock.GetUtcNow();
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        foreach (var line in lines.OrderBy(static l => l.ProductId))
        {
            long quantity = line.Quantity;
            var inserted = await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO inventory.reservations (tenant_id, order_id, product_id, quantity, status, created_at)
                VALUES ({tenantId}, {orderId}, {line.ProductId}, {quantity}, {ReservationStatusNames.Reserved}, {now})
                ON CONFLICT (tenant_id, order_id, product_id) DO NOTHING
                """,
                cancellationToken);
            if (inserted == 0)
            {
                // A row for this order already exists (an earlier call, or a concurrent one that has now committed):
                // change nothing here and answer from what was committed.
                await transaction.RollbackAsync(cancellationToken);
                return await AlreadyReservedAsync(tenantId, orderId, lines, cancellationToken);
            }

            var reserved = await db.Database.ExecuteSqlAsync(
                $"""
                UPDATE inventory.stock_balances
                SET reserved = reserved + {quantity}, updated_at = {now}
                WHERE tenant_id = {tenantId} AND product_id = {line.ProductId} AND on_hand - reserved >= {quantity}
                """,
                cancellationToken);
            if (reserved == 0)
            {
                // Not enough available (or no balance at all): nothing of this order stays reserved.
                await transaction.RollbackAsync(cancellationToken);
                return ReservationOutcome.InsufficientStock;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return ReservationOutcome.Reserved;
    }

    // The order's committed rows must be exactly these lines, all still held; otherwise repeating the call would report a
    // reservation that does not hold the units the caller relies on.
    private async Task<ReservationOutcome> AlreadyReservedAsync(Guid tenantId, Guid orderId, IReadOnlyCollection<ReservationLine> lines, CancellationToken cancellationToken)
    {
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        var rows = await db.Reservations.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.OrderId == orderId)
            .Select(r => new { r.ProductId, r.Quantity, r.Status })
            .ToListAsync(cancellationToken);
        var held = rows.All(static r => r.Status == ReservationStatus.Reserved)
            && rows.Select(static r => (r.ProductId, r.Quantity)).Order().SequenceEqual(lines.Select(static l => (l.ProductId, (long)l.Quantity)).Order());
        return held
            ? ReservationOutcome.Reserved
            : throw new InvalidOperationException("The order's reservation was released or covers other lines; it cannot be reserved again.");
    }

    public async Task ReleaseAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var now = clock.GetUtcNow();
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        // Lock the order's reserved rows in product order, so a concurrent release waits here and then finds nothing.
        var held = await db.Database.SqlQuery<HeldReservationRow>(
            $"""
            SELECT product_id AS "ProductId", quantity AS "Quantity"
            FROM inventory.reservations
            WHERE tenant_id = {tenantId} AND order_id = {orderId} AND status = {ReservationStatusNames.Reserved}
            ORDER BY product_id
            FOR UPDATE
            """).ToListAsync(cancellationToken);
        foreach (var row in held)
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                UPDATE inventory.reservations SET status = {ReservationStatusNames.Released}, released_at = {now}
                WHERE tenant_id = {tenantId} AND order_id = {orderId} AND product_id = {row.ProductId}
                """,
                cancellationToken);
            await db.Database.ExecuteSqlAsync(
                $"""
                UPDATE inventory.stock_balances SET reserved = reserved - {row.Quantity}, updated_at = {now}
                WHERE tenant_id = {tenantId} AND product_id = {row.ProductId}
                """,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
