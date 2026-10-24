using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TenantFlow.Payments.Domain.Payments;

namespace TenantFlow.Payments.Infrastructure.Persistence.Configurations;

/// <summary>
/// The payment row. The order or subscription is referenced by id only: no foreign key crosses into another module
/// (ADR-0012). Exactly one subject is set. One payment per order and tenant, and one per subscription period and attempt,
/// each enforced by a unique index, so no second charge can even be stored (ADR-0023 decision 3). The status is stored by
/// its stable name, and the database refuses any other.
/// </summary>
internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    private static readonly ValueConverter<PaymentStatus, string> StatusConversion =
        new(status => status.ToName(), name => PaymentStatusNames.Parse(name));

    public void Configure(EntityTypeBuilder<Payment> payment)
    {
        payment.ToTable("payments", table =>
        {
            table.HasCheckConstraint("ck_payments_status", $"status IN ({string.Join(", ", PaymentStatusNames.All.Select(static n => $"'{n}'"))})");
            table.HasCheckConstraint("ck_payments_amount", "amount > 0");
            table.HasCheckConstraint("ck_payments_refunded_amount", "refunded_amount >= 0 AND refunded_amount <= amount");
            table.HasCheckConstraint("ck_payments_subject",
                "(order_id IS NOT NULL AND subscription_id IS NULL AND billing_period IS NULL AND charge_attempt IS NULL) OR " +
                "(order_id IS NULL AND subscription_id IS NOT NULL AND billing_period >= 1 AND charge_attempt >= 1)");
        });
        payment.HasKey(p => p.Id).HasName("pk_payments");
        payment.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
        payment.Property(p => p.TenantId).HasColumnName("tenant_id");
        payment.Property(p => p.OrderId).HasColumnName("order_id");
        payment.Property(p => p.SubscriptionId).HasColumnName("subscription_id");
        payment.Property(p => p.BillingPeriod).HasColumnName("billing_period");
        payment.Property(p => p.ChargeAttempt).HasColumnName("charge_attempt");
        payment.Property(p => p.OwnerMembershipId).HasColumnName("owner_membership_id");
        payment.Property(p => p.Amount).HasColumnName("amount").HasPrecision(19, 4);
        payment.Property(p => p.Currency).HasColumnName("currency").HasColumnType("character(3)").IsRequired();
        payment.Property(p => p.Status).HasColumnName("status").HasConversion(StatusConversion).HasMaxLength(32);
        payment.Property(p => p.Provider).HasColumnName("provider").HasMaxLength(32).IsRequired();
        payment.Property(p => p.ProviderPaymentId).HasColumnName("provider_payment_id").HasMaxLength(255);
        payment.Property(p => p.PaymentMethod).HasColumnName("payment_method").HasMaxLength(Payment.MaxPaymentMethodLength).IsRequired();
        payment.Property(p => p.FailureCode).HasColumnName("failure_code").HasMaxLength(64);
        payment.Property(p => p.RefundedAmount).HasColumnName("refunded_amount").HasPrecision(19, 4).HasDefaultValue(0m);
        payment.Property(p => p.CreatedAt).HasColumnName("created_at");
        payment.Property(p => p.UpdatedAt).HasColumnName("updated_at");
        payment.HasIndex(p => new { p.TenantId, p.OrderId }).IsUnique().HasDatabaseName("ux_payments_tenant_id_order_id");
        payment.HasIndex(p => new { p.TenantId, p.SubscriptionId, p.BillingPeriod, p.ChargeAttempt })
            .IsUnique()
            .HasDatabaseName("ux_payments_subscription_charge")
            .HasFilter("subscription_id IS NOT NULL");
    }
}
