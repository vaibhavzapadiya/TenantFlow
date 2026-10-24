using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantFlow.Inventory.Domain.Stock;

namespace TenantFlow.Inventory.Infrastructure.Persistence.Configurations;

/// <summary>
/// One row per (tenant, order, product): the key that makes reserving idempotent. The order is referenced by id only; no
/// foreign key crosses into the orders schema (ADR-0012). Rows are never deleted: the migration revokes DELETE.
/// </summary>
internal sealed class StockReservationConfiguration : IEntityTypeConfiguration<StockReservation>
{
    public void Configure(EntityTypeBuilder<StockReservation> reservation)
    {
        reservation.ToTable("reservations", table =>
        {
            table.HasCheckConstraint("ck_reservations_quantity", "quantity > 0");
            table.HasCheckConstraint("ck_reservations_status", $"status IN ('{ReservationStatusNames.Reserved}', '{ReservationStatusNames.Released}')");
        });
        reservation.HasKey(r => new { r.TenantId, r.OrderId, r.ProductId }).HasName("pk_reservations");
        reservation.Property(r => r.TenantId).HasColumnName("tenant_id");
        reservation.Property(r => r.OrderId).HasColumnName("order_id");
        reservation.Property(r => r.ProductId).HasColumnName("product_id");
        reservation.Property(r => r.Quantity).HasColumnName("quantity");
        reservation.Property(r => r.Status).HasColumnName("status").HasConversion(ReservationStatusNames.Conversion).HasMaxLength(16);
        reservation.Property(r => r.CreatedAt).HasColumnName("created_at");
        reservation.Property(r => r.ReleasedAt).HasColumnName("released_at");
    }
}
