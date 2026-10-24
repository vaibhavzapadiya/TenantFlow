using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantFlow.Inventory.Domain.Stock;

namespace TenantFlow.Inventory.Infrastructure.Persistence.Configurations;

/// <summary>Append-only: the migration revokes UPDATE, DELETE and TRUNCATE on this table from the application role.</summary>
internal sealed class StockAdjustmentConfiguration : IEntityTypeConfiguration<StockAdjustment>
{
    public void Configure(EntityTypeBuilder<StockAdjustment> adjustment)
    {
        adjustment.ToTable("stock_adjustments", table => table.HasCheckConstraint("ck_stock_adjustments_delta", "delta <> 0"));
        adjustment.HasKey(a => a.Id).HasName("pk_stock_adjustments");
        adjustment.Property(a => a.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        adjustment.Property(a => a.TenantId).HasColumnName("tenant_id");
        adjustment.Property(a => a.ProductId).HasColumnName("product_id");
        adjustment.Property(a => a.Delta).HasColumnName("delta");
        adjustment.Property(a => a.Reason).HasColumnName("reason").HasMaxLength(StockAdjustment.ReasonMaxLength).IsRequired();
        adjustment.Property(a => a.ActorMembershipId).HasColumnName("actor_membership_id");
        adjustment.Property(a => a.OccurredAt).HasColumnName("occurred_at");
        adjustment.HasIndex(a => new { a.TenantId, a.ProductId, a.OccurredAt }).HasDatabaseName("ix_stock_adjustments_tenant_id_product_id_occurred_at");
    }
}
