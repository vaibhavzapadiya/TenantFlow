using Microsoft.EntityFrameworkCore;

namespace TenantFlow.BuildingBlocks.Scheduling.Persistence;

/// <summary>
/// The scheduler's schema, <c>scheduler</c>, for its migrations only (ADR-0012, ADR-0023): Quartz.NET reads and writes its
/// <c>qrtz_*</c> tables itself, so the context maps no entity. Only the Migrator applies its migrations. No tenant data
/// lives here: the jobs name module schemas, never a tenant or a subject.
/// </summary>
public sealed class SchedulerDbContext(DbContextOptions<SchedulerDbContext> options) : DbContext(options)
{
    public const string Schema = "scheduler";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
    }
}
