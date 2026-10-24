using Microsoft.EntityFrameworkCore;
using TenantFlow.BuildingBlocks.Messaging.Inbox;
using TenantFlow.BuildingBlocks.Messaging.Outbox;
using TenantFlow.BuildingBlocks.Messaging.Timers;
using TenantFlow.Orders.Domain.Orders;
using TenantFlow.Orders.Infrastructure.Persistence.Configurations;

namespace TenantFlow.Orders.Infrastructure.Persistence;

/// <summary>
/// The Orders module's database context. It maps only to the <c>orders</c> schema, and no other module may use it
/// (ADR-0002, ADR-0012). Only the Migrator applies its migrations.
/// </summary>
public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public const string Schema = "orders";

    internal DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new OrderConfiguration());
        modelBuilder.ApplyConfiguration(new OrderLineConfiguration());
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new ProcessedMessageConfiguration());
        modelBuilder.ApplyConfiguration(new WorkflowTimerConfiguration());
    }
}
