using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TenantFlow.BuildingBlocks.Messaging.Outbox;

/// <summary>
/// The <c>outbox_messages</c> table, in the schema of the module whose context applies it (ADR-0012, ADR-0021).
/// It is not under row-level security: the publisher reads every tenant's rows, and each row carries its tenant id.
/// </summary>
public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public const string Table = "outbox_messages";

    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable(Table, table =>
        {
            table.HasCheckConstraint("ck_outbox_messages_version", "version >= 1");
            table.HasCheckConstraint("ck_outbox_messages_attempts", "attempts >= 0");
        });
        builder.HasKey(m => m.Id).HasName("pk_outbox_messages");
        builder.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(m => m.TenantId).HasColumnName("tenant_id");
        builder.Property(m => m.Type).HasColumnName("type").HasMaxLength(128);
        builder.Property(m => m.Version).HasColumnName("version");
        builder.Property(m => m.Payload).HasColumnName("payload").HasColumnType("jsonb");
        builder.Property(m => m.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
        builder.Property(m => m.TraceParent).HasColumnName("traceparent").HasMaxLength(64);
        builder.Property(m => m.OccurredAt).HasColumnName("occurred_at");
        builder.Property(m => m.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        builder.Property(m => m.LockedUntil).HasColumnName("locked_until");
        builder.Property(m => m.PublishedAt).HasColumnName("published_at");
        builder.Property(m => m.LastError).HasColumnName("last_error").HasMaxLength(1024);
        // The publisher's scan: pending rows, oldest first. Published rows drop out of the index.
        builder.HasIndex(m => m.OccurredAt).HasDatabaseName("ix_outbox_messages_pending").HasFilter("published_at IS NULL");
    }
}
