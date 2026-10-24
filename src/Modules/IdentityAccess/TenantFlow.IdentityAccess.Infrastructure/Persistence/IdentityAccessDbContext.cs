using Microsoft.EntityFrameworkCore;
using TenantFlow.BuildingBlocks.Messaging.Outbox;
using TenantFlow.IdentityAccess.Domain.Auditing;
using TenantFlow.IdentityAccess.Domain.Memberships;
using TenantFlow.IdentityAccess.Infrastructure.Persistence.Configurations;

namespace TenantFlow.IdentityAccess.Infrastructure.Persistence;

/// <summary>
/// The IdentityAccess module's database context. It maps only to the <c>identity_access</c> schema, and no other
/// module may use it (ADR-0002, ADR-0012). Only the Migrator applies its migrations.
/// </summary>
public sealed class IdentityAccessDbContext(DbContextOptions<IdentityAccessDbContext> options) : DbContext(options)
{
    public const string Schema = "identity_access";

    internal DbSet<Membership> Memberships => Set<Membership>();

    internal DbSet<MembershipAuditRecord> AuditRecords => Set<MembershipAuditRecord>();

    /// <summary>The module's outbox (ADR-0021): audit events, written in the change's own SaveChanges (ADR-0026).</summary>
    internal DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new MembershipConfiguration());
        modelBuilder.ApplyConfiguration(new MembershipAuditRecordConfiguration());
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
    }
}
