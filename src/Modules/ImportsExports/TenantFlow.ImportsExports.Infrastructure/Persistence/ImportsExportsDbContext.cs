using Microsoft.EntityFrameworkCore;
using TenantFlow.BuildingBlocks.Messaging.Outbox;
using TenantFlow.BuildingBlocks.Messaging.Timers;
using TenantFlow.ImportsExports.Domain.Files;
using TenantFlow.ImportsExports.Domain.Jobs;
using TenantFlow.ImportsExports.Infrastructure.Persistence.Configurations;

namespace TenantFlow.ImportsExports.Infrastructure.Persistence;

/// <summary>
/// The ImportsExports module's database context. It maps only to the <c>imports_exports</c> schema, and no other module
/// may use it (ADR-0002, ADR-0012). Only the Migrator applies its migrations.
/// </summary>
public sealed class ImportsExportsDbContext(DbContextOptions<ImportsExportsDbContext> options) : DbContext(options)
{
    public const string Schema = "imports_exports";

    internal DbSet<StoredFile> Files => Set<StoredFile>();

    internal DbSet<ImportJob> ImportJobs => Set<ImportJob>();

    internal DbSet<ImportRowErrorRow> ImportRowErrors => Set<ImportRowErrorRow>();

    internal DbSet<ExportJob> ExportJobs => Set<ExportJob>();

    /// <summary>The module's outbox (ADR-0021): audit events, written in the request's own transaction (ADR-0026).</summary>
    internal DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new StoredFileConfiguration());
        modelBuilder.ApplyConfiguration(new ImportJobConfiguration());
        modelBuilder.ApplyConfiguration(new ImportRowErrorConfiguration());
        modelBuilder.ApplyConfiguration(new ExportJobConfiguration());
        modelBuilder.ApplyConfiguration(new WorkflowTimerConfiguration());
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
    }
}
