using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantFlow.Inventory.Domain.Stock;

namespace TenantFlow.Inventory.Infrastructure.Persistence.Configurations;

/// <summary>One row per (tenant, product). The product is referenced by id only: no foreign key crosses into catalog (ADR-0012).</summary>
internal sealed class StockBalanceConfiguration : IEntityTypeConfiguration<StockBalance>
{
    public void Configure(EntityTypeBuilder<StockBalance> balance)
    {
        // The database keeps stock non-negative even if a write path forgot the conditional update (SPEC-0003 invariant).
        // Reserved units are part of what is on hand, never more (SPEC-0004 invariant), whatever the write path.
        balance.ToTable("stock_balances", table =>
        {
            table.HasCheckConstraint("ck_stock_balances_on_hand", "on_hand >= 0");
            table.HasCheckConstraint("ck_stock_balances_reserved", "reserved >= 0 AND reserved <= on_hand");
        });
        balance.HasKey(b => new { b.TenantId, b.ProductId }).HasName("pk_stock_balances");
        balance.Property(b => b.TenantId).HasColumnName("tenant_id");
        balance.Property(b => b.ProductId).HasColumnName("product_id");
        balance.Property(b => b.OnHand).HasColumnName("on_hand");
        balance.Property(b => b.Reserved).HasColumnName("reserved").HasDefaultValue(0L);
        balance.Property(b => b.UpdatedAt).HasColumnName("updated_at");
    }
}
