using Microsoft.EntityFrameworkCore;
using TenantFlow.BuildingBlocks.Messaging.Timers;
using TenantFlow.Subscriptions.Domain.Plans;
using TenantFlow.Subscriptions.Domain.Subscriptions;
using TenantFlow.Subscriptions.Infrastructure.Persistence.Configurations;

namespace TenantFlow.Subscriptions.Infrastructure.Persistence;

/// <summary>
/// The Subscriptions module's database context. It maps only to the <c>subscriptions</c> schema, and no other module may
/// use it (ADR-0002, ADR-0012). Only the Migrator applies its migrations.
/// </summary>
public sealed class SubscriptionsDbContext(DbContextOptions<SubscriptionsDbContext> options) : DbContext(options)
{
    public const string Schema = "subscriptions";

    internal DbSet<Plan> Plans => Set<Plan>();

    internal DbSet<Subscription> Subscriptions => Set<Subscription>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new PlanConfiguration());
        modelBuilder.ApplyConfiguration(new SubscriptionConfiguration());
        modelBuilder.ApplyConfiguration(new WorkflowTimerConfiguration());
    }
}
