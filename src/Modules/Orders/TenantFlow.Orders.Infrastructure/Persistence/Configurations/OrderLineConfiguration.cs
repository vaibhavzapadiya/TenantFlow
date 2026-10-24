using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantFlow.Catalog.Contracts;
using TenantFlow.Orders.Domain.Orders;

namespace TenantFlow.Orders.Infrastructure.Persistence.Configurations;

/// <summary>
/// A line row: a surrogate key, so replacing a draft's lines deletes the old rows and inserts new ones without key
/// collisions. The tenant id is repeated on the line so row-level security filters it on its own (ADR-0011). The SKU
/// and name columns are sized from Catalog's contract limits, so every snapshot Catalog can produce fits.
/// </summary>
internal sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> line)
    {
        line.ToTable("order_lines", table =>
        {
            table.HasCheckConstraint("ck_order_lines_quantity", $"quantity BETWEEN 1 AND {Order.MaxQuantity}");
            table.HasCheckConstraint("ck_order_lines_unit_price", "unit_price >= 0");
        });
        line.HasKey(l => l.Id).HasName("pk_order_lines");
        line.Property(l => l.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        line.Property(l => l.TenantId).HasColumnName("tenant_id");
        line.Property(l => l.OrderId).HasColumnName("order_id");
        line.Property(l => l.LineNo).HasColumnName("line_no");
        line.Property(l => l.ProductId).HasColumnName("product_id");
        line.Property(l => l.Sku).HasColumnName("sku").HasMaxLength(ProductLimits.SkuMaxLength).IsRequired();
        line.Property(l => l.Name).HasColumnName("name").HasMaxLength(ProductLimits.NameMaxLength).IsRequired();
        line.Property(l => l.UnitPrice).HasColumnName("unit_price").HasPrecision(19, 4);
        line.Property(l => l.Quantity).HasColumnName("quantity");
        line.Property(l => l.LineTotal).HasColumnName("line_total").HasPrecision(19, 4);
        line.HasIndex(l => l.OrderId).HasDatabaseName("ix_order_lines_order_id");
    }
}
