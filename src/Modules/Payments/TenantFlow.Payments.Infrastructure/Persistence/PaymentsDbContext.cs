using Microsoft.EntityFrameworkCore;
using TenantFlow.BuildingBlocks.Messaging.Outbox;
using TenantFlow.BuildingBlocks.Messaging.Timers;
using TenantFlow.Payments.Domain.Payments;
using TenantFlow.Payments.Domain.Refunds;
using TenantFlow.Payments.Infrastructure.Persistence.Configurations;

namespace TenantFlow.Payments.Infrastructure.Persistence;

/// <summary>
/// The Payments module's database context. It maps only to the <c>payments</c> schema, and no other module may use it
/// (ADR-0002, ADR-0012). Only the Migrator applies its migrations.
/// </summary>
public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public const string Schema = "payments";

    internal DbSet<Payment> Payments => Set<Payment>();

    internal DbSet<IdempotencyKeyRow> IdempotencyKeys => Set<IdempotencyKeyRow>();

    internal DbSet<Refund> Refunds => Set<Refund>();

    internal DbSet<WebhookEventRow> WebhookEvents => Set<WebhookEventRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new PaymentConfiguration());
        modelBuilder.ApplyConfiguration(new IdempotencyKeyConfiguration());
        modelBuilder.ApplyConfiguration(new RefundConfiguration());
        modelBuilder.ApplyConfiguration(new WebhookEventConfiguration());
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new WorkflowTimerConfiguration());
    }
}
