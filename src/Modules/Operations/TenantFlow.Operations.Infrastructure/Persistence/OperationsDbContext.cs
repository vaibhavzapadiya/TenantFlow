using Microsoft.EntityFrameworkCore;
using TenantFlow.BuildingBlocks.Messaging.Outbox;
using TenantFlow.Operations.Domain.SupportGrants;
using TenantFlow.Operations.Infrastructure.Persistence.Configurations;

namespace TenantFlow.Operations.Infrastructure.Persistence;

/// <summary>
/// The Operations module's database context. It maps only to the <c>operations</c> schema, and no other module may use it
/// (ADR-0002, ADR-0012). Only the Migrator applies its migrations.
/// </summary>
public sealed class OperationsDbContext(DbContextOptions<OperationsDbContext> options) : DbContext(options)
{
    public const string Schema = "operations";

    internal DbSet<SupportGrant> SupportGrants => Set<SupportGrant>();

    /// <summary>The module's outbox (ADR-0021): audit events of grants and accesses (ADR-0026).</summary>
    internal DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new SupportGrantConfiguration());
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
    }
}
