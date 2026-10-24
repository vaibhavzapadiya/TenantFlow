using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantFlow.Payments.Domain.Payments;

namespace TenantFlow.Payments.Infrastructure.Persistence.Configurations;

/// <summary>The idempotency-key row: one per (tenant, operation, key), so a key means one request per tenant only.</summary>
internal sealed class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKeyRow>
{
    public void Configure(EntityTypeBuilder<IdempotencyKeyRow> key)
    {
        key.ToTable("idempotency_keys");
        key.HasKey(k => new { k.TenantId, k.Operation, k.Key }).HasName("pk_idempotency_keys");
        key.Property(k => k.TenantId).HasColumnName("tenant_id");
        key.Property(k => k.Operation).HasColumnName("operation").HasMaxLength(64);
        key.Property(k => k.Key).HasColumnName("key").HasMaxLength(Payment.MaxIdempotencyKeyLength);
        key.Property(k => k.RequestHash).HasColumnName("request_hash").HasColumnType("character(64)").IsRequired();
        key.Property(k => k.PaymentId).HasColumnName("payment_id");
        key.Property(k => k.RefundId).HasColumnName("refund_id");
        key.Property(k => k.ResponseBody).HasColumnName("response_body").HasColumnType("jsonb");
        key.Property(k => k.CreatedAt).HasColumnName("created_at");
        key.Property(k => k.CompletedAt).HasColumnName("completed_at");
    }
}
