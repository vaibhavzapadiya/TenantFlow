using Microsoft.EntityFrameworkCore;
using TenantFlow.Audit.Domain.Records;
using TenantFlow.Audit.Infrastructure.Persistence.Configurations;
using TenantFlow.BuildingBlocks.Messaging.Inbox;

namespace TenantFlow.Audit.Infrastructure.Persistence;

/// <summary>
/// The Audit module's database context. It maps only to the <c>audit</c> schema, and no other module may use it
/// (ADR-0002, ADR-0012). Only the Migrator applies its migrations.
/// </summary>
public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    public const string Schema = "audit";

    internal DbSet<AuditRecord> Records => Set<AuditRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new AuditRecordConfiguration());
        modelBuilder.ApplyConfiguration(new ProcessedMessageConfiguration());
    }
}
