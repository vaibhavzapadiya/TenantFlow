using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TenantFlow.BuildingBlocks.Messaging.Timers;

/// <summary>
/// The <c>workflow_timers</c> table, in the schema of the module whose context applies it (ADR-0012, ADR-0022). Not under
/// row-level security: the runner reads every tenant's due timers, and each row carries its tenant id.
/// </summary>
public sealed class WorkflowTimerConfiguration : IEntityTypeConfiguration<WorkflowTimer>
{
    public const string Table = "workflow_timers";

    public void Configure(EntityTypeBuilder<WorkflowTimer> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable(Table, table =>
        {
            table.HasCheckConstraint("ck_workflow_timers_attempts", "attempts >= 0");
            table.HasCheckConstraint("ck_workflow_timers_failures", "failures >= 0");
        });
        builder.HasKey(t => t.Id).HasName("pk_workflow_timers");
        builder.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(t => t.TenantId).HasColumnName("tenant_id");
        builder.Property(t => t.Kind).HasColumnName("kind").HasMaxLength(64);
        builder.Property(t => t.SubjectId).HasColumnName("subject_id");
        builder.Property(t => t.DueAt).HasColumnName("due_at");
        builder.Property(t => t.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        builder.Property(t => t.LockedUntil).HasColumnName("locked_until");
        builder.Property(t => t.CompletedAt).HasColumnName("completed_at");
        builder.Property(t => t.Failures).HasColumnName("failures").HasDefaultValue(0);
        builder.Property(t => t.DeadAt).HasColumnName("dead_at");
        // One timer per kind and subject: scheduling again moves it instead of adding a second.
        builder.HasIndex(t => new { t.Kind, t.SubjectId }).IsUnique().HasDatabaseName("ux_workflow_timers_kind_subject_id");
        // The runner's scan: open timers by due time. Completed ones drop out of the index; dead ones are few (ADR-0023).
        builder.HasIndex(t => t.DueAt).HasDatabaseName("ix_workflow_timers_open").HasFilter("completed_at IS NULL");
    }
}
