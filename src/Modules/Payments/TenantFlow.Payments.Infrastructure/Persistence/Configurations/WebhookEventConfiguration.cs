using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TenantFlow.Payments.Infrastructure.Persistence.Configurations;

/// <summary>
/// The received-webhook row. Its key is global (provider, event id), not per tenant: a provider's event id is unique, and
/// a uniqueness check is not filtered by row-level security, so a replay is recognised whichever tenant it names.
/// </summary>
internal sealed class WebhookEventConfiguration : IEntityTypeConfiguration<WebhookEventRow>
{
    public void Configure(EntityTypeBuilder<WebhookEventRow> webhook)
    {
        webhook.ToTable("webhook_events", table => table.HasCheckConstraint("ck_webhook_events_outcome", "outcome IN ('applied', 'ignored')"));
        webhook.HasKey(w => new { w.Provider, w.EventId }).HasName("pk_webhook_events");
        webhook.Property(w => w.Provider).HasColumnName("provider").HasMaxLength(32);
        webhook.Property(w => w.EventId).HasColumnName("event_id").HasMaxLength(255);
        webhook.Property(w => w.TenantId).HasColumnName("tenant_id");
        webhook.Property(w => w.PaymentId).HasColumnName("payment_id");
        webhook.Property(w => w.Outcome).HasColumnName("outcome").HasMaxLength(16);
        webhook.Property(w => w.ReceivedAt).HasColumnName("received_at");
    }
}
