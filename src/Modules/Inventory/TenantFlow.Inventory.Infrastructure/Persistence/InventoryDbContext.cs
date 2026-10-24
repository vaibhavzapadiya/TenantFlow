using Microsoft.EntityFrameworkCore;
using TenantFlow.Inventory.Domain.Stock;
using TenantFlow.Inventory.Infrastructure.Persistence.Configurations;

namespace TenantFlow.Inventory.Infrastructure.Persistence;

/// <summary>
/// The Inventory module's database context. It maps only to the <c>inventory</c> schema, and no other module may use
/// it (ADR-0002, ADR-0012). Only the Migrator applies its migrations.
/// </summary>
public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public const string Schema = "inventory";

    internal DbSet<StockBalance> Balances => Set<StockBalance>();

    internal DbSet<StockAdjustment> Adjustments => Set<StockAdjustment>();

    internal DbSet<StockReservation> Reservations => Set<StockReservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new StockBalanceConfiguration());
        modelBuilder.ApplyConfiguration(new StockAdjustmentConfiguration());
        modelBuilder.ApplyConfiguration(new StockReservationConfiguration());
    }
}
