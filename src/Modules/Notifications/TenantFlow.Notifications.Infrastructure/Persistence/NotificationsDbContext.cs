using Microsoft.EntityFrameworkCore;
using TenantFlow.BuildingBlocks.Messaging.Inbox;
using TenantFlow.Notifications.Domain.Notifications;
using TenantFlow.Notifications.Infrastructure.Persistence.Configurations;

namespace TenantFlow.Notifications.Infrastructure.Persistence;

/// <summary>
/// The Notifications module's database context. It maps only to the <c>notifications</c> schema, and no other module may
/// use it (ADR-0002, ADR-0012). Only the Migrator applies its migrations.
/// </summary>
public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public const string Schema = "notifications";

    internal DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new NotificationConfiguration());
        modelBuilder.ApplyConfiguration(new ProcessedMessageConfiguration());
    }
}
