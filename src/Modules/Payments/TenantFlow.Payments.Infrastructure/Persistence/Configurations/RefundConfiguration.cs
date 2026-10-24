using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TenantFlow.Payments.Domain.Payments;
using TenantFlow.Payments.Domain.Refunds;

namespace TenantFlow.Payments.Infrastructure.Persistence.Configurations;

/// <summary>
/// The refund row (SPEC-0007, ADR-0023 decision 4), with its audit fields. It references its payment in the same schema; at
/// most one cancellation refund per payment that has not failed, by a partial unique index (a failed one may be followed
/// by another, review fix R3). Its status uses the payment status names.
/// </summary>
internal sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    private static readonly ValueConverter<PaymentStatus, string> StatusConversion =
        new(status => status.ToName(), name => PaymentStatusNames.Parse(name));

    public void Configure(EntityTypeBuilder<Refund> refund)
    {
        refund.ToTable("refunds", table =>
        {
            table.HasCheckConstraint("ck_refunds_status", $"status IN ({string.Join(", ", PaymentStatusNames.All.Select(static n => $"'{n}'"))})");
            table.HasCheckConstraint("ck_refunds_amount", "amount > 0");
            table.HasCheckConstraint("ck_refunds_kind", $"kind IN ('{Refund.RefundKind}', '{Refund.CancellationKind}')");
        });
        refund.HasKey(r => r.Id).HasName("pk_refunds");
        refund.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        refund.Property(r => r.TenantId).HasColumnName("tenant_id");
        refund.Property(r => r.PaymentId).HasColumnName("payment_id");
        refund.Property(r => r.OrderId).HasColumnName("order_id");
        refund.Property(r => r.Amount).HasColumnName("amount").HasPrecision(19, 4);
        refund.Property(r => r.Currency).HasColumnName("currency").HasColumnType("character(3)").IsRequired();
        refund.Property(r => r.Status).HasColumnName("status").HasConversion(StatusConversion).HasMaxLength(32);
        refund.Property(r => r.Kind).HasColumnName("kind").HasMaxLength(16).IsRequired();
        refund.Property(r => r.Reason).HasColumnName("reason").HasMaxLength(Refund.MaxReasonLength).IsRequired();
        refund.Property(r => r.RequestedByMembershipId).HasColumnName("requested_by_membership_id");
        refund.Property(r => r.Provider).HasColumnName("provider").HasMaxLength(32).IsRequired();
        refund.Property(r => r.ProviderRefundId).HasColumnName("provider_refund_id").HasMaxLength(255);
        refund.Property(r => r.FailureCode).HasColumnName("failure_code").HasMaxLength(64);
        refund.Property(r => r.CreatedAt).HasColumnName("created_at");
        refund.Property(r => r.UpdatedAt).HasColumnName("updated_at");
        refund.HasOne<Payment>().WithMany().HasForeignKey(r => r.PaymentId).HasConstraintName("fk_refunds_payments_payment_id").OnDelete(DeleteBehavior.Restrict);
        refund.HasIndex(r => new { r.TenantId, r.PaymentId }).HasDatabaseName("ix_refunds_tenant_id_payment_id");
        refund.HasIndex(r => r.PaymentId)
            .IsUnique()
            .HasDatabaseName("ux_refunds_one_cancellation_per_payment")
            .HasFilter($"kind = '{Refund.CancellationKind}' AND status <> '{PaymentStatusNames.Failed}'");
    }
}
