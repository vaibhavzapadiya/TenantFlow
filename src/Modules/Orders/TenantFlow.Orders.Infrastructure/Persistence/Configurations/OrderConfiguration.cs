using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TenantFlow.Orders.Domain.Orders;

namespace TenantFlow.Orders.Infrastructure.Persistence.Configurations;

/// <summary>
/// The order row. Products are referenced from its lines by id only: no foreign key crosses into catalog (ADR-0012).
/// The status is stored by its stable name, and the database refuses any other.
/// </summary>
internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    private static readonly ValueConverter<OrderStatus, string> StatusConversion =
        new(status => status.ToName(), name => OrderStatusNames.Parse(name));

    public void Configure(EntityTypeBuilder<Order> order)
    {
        order.ToTable("orders", table =>
        {
            table.HasCheckConstraint("ck_orders_status", $"status IN ({string.Join(", ", OrderStatusNames.All.Select(static n => $"'{n}'"))})");
            table.HasCheckConstraint("ck_orders_total_amount", "total_amount >= 0");
        });
        order.HasKey(o => o.Id).HasName("pk_orders");
        order.Property(o => o.Id).HasColumnName("id").ValueGeneratedNever();
        order.Property(o => o.TenantId).HasColumnName("tenant_id");
        order.Property(o => o.CustomerMembershipId).HasColumnName("customer_membership_id");
        order.Property(o => o.Status).HasColumnName("status").HasConversion(StatusConversion).HasMaxLength(32);
        order.Property(o => o.Currency).HasColumnName("currency").HasColumnType("character(3)").IsRequired();
        order.Property(o => o.TotalAmount).HasColumnName("total_amount").HasPrecision(19, 4);
        order.Property(o => o.RejectionReason).HasColumnName("rejection_reason").HasMaxLength(64);
        order.Property(o => o.CancellationReason).HasColumnName("cancellation_reason").HasMaxLength(Order.MaxCancellationReasonLength);
        order.Property(o => o.CancellationRequestedBy).HasColumnName("cancellation_requested_by");
        order.Property(o => o.PaymentStartedAt).HasColumnName("payment_started_at");
        order.Property(o => o.CreatedAt).HasColumnName("created_at");
        order.Property(o => o.UpdatedAt).HasColumnName("updated_at");
        // Optimistic concurrency on PostgreSQL's row version: concurrent edits cannot both win (SPEC-0004 criterion 8).
        order.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        order.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.OrderId).HasConstraintName("fk_order_lines_orders").OnDelete(DeleteBehavior.Cascade);
        order.Navigation(o => o.Lines).HasField("_lines").UsePropertyAccessMode(PropertyAccessMode.Field);
        order.HasIndex(o => new { o.TenantId, o.CustomerMembershipId }).HasDatabaseName("ix_orders_tenant_id_customer_membership_id");
    }
}
