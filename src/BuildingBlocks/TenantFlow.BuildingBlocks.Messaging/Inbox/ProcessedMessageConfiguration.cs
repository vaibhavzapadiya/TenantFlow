using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TenantFlow.BuildingBlocks.Messaging.Inbox;

/// <summary>
/// The <c>processed_messages</c> table, in the schema of the consuming module whose context applies it (ADR-0021). The key
/// is the consumer and the message id; the tenant column is there for row-level security, which the module's migration
/// enables and forces.
/// </summary>
public sealed class ProcessedMessageConfiguration : IEntityTypeConfiguration<ProcessedMessage>
{
    public const string Table = "processed_messages";

    public void Configure(EntityTypeBuilder<ProcessedMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable(Table);
        builder.HasKey(m => new { m.Consumer, m.MessageId }).HasName("pk_processed_messages");
        builder.Property(m => m.Consumer).HasColumnName("consumer").HasMaxLength(128);
        builder.Property(m => m.MessageId).HasColumnName("message_id");
        builder.Property(m => m.TenantId).HasColumnName("tenant_id");
        builder.Property(m => m.ProcessedAt).HasColumnName("processed_at");
    }
}
