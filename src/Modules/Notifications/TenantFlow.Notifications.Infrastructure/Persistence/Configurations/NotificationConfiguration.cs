using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantFlow.Notifications.Domain.Notifications;

namespace TenantFlow.Notifications.Infrastructure.Persistence.Configurations;

/// <summary>
/// The notification row. Orders and memberships are referenced by id only: no foreign key crosses into another module's
/// schema (ADR-0012). The kind is stored by its stable name, and the database refuses any other.
/// </summary>
internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> notification)
    {
        notification.ToTable("notifications", table =>
            table.HasCheckConstraint("ck_notifications_kind", $"kind IN ({string.Join(", ", NotificationKinds.All.Select(static k => $"'{k}'"))})"));
        notification.HasKey(n => n.Id).HasName("pk_notifications");
        notification.Property(n => n.Id).HasColumnName("id").ValueGeneratedNever();
        notification.Property(n => n.TenantId).HasColumnName("tenant_id");
        notification.Property(n => n.Kind).HasColumnName("kind").HasMaxLength(32);
        notification.Property(n => n.OrderId).HasColumnName("order_id");
        notification.Property(n => n.RecipientMembershipId).HasColumnName("recipient_membership_id");
        notification.Property(n => n.MessageId).HasColumnName("message_id");
        notification.Property(n => n.CreatedAt).HasColumnName("created_at");
        notification.HasIndex(n => new { n.TenantId, n.RecipientMembershipId }).HasDatabaseName("ix_notifications_tenant_id_recipient_membership_id");
    }
}
