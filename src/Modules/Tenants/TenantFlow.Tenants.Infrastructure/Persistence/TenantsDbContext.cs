using Microsoft.EntityFrameworkCore;
using TenantFlow.BuildingBlocks.Messaging.Outbox;
using TenantFlow.Tenants.Domain.Auditing;
using TenantFlow.Tenants.Domain.Tenants;
using TenantFlow.Tenants.Infrastructure.Persistence.Configurations;

namespace TenantFlow.Tenants.Infrastructure.Persistence;

/// <summary>
/// The Tenants module's database context. It maps only to the <c>tenants</c> schema, and no other
/// module may use it (ADR-0002, ADR-0012). Only the Migrator applies its migrations.
/// </summary>
public sealed class TenantsDbContext(DbContextOptions<TenantsDbContext> options) : DbContext(options)
{
    public const string Schema = "tenants";

    internal DbSet<Tenant> Tenants => Set<Tenant>();

    internal DbSet<TenantAuditRecord> AuditRecords => Set<TenantAuditRecord>();

    /// <summary>The module's outbox (ADR-0021): audit events, written in the change's own SaveChanges (ADR-0026).</summary>
    internal DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new TenantConfiguration());
        modelBuilder.ApplyConfiguration(new TenantAuditRecordConfiguration());
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
    }
}
