using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TenantFlow.Subscriptions.Domain.Subscriptions;

namespace TenantFlow.Subscriptions.Infrastructure.Persistence.Configurations;

/// <summary>
/// The subscription row. The subscriber is referenced by membership id only: no foreign key crosses into identity_access
/// (ADR-0012). One live subscription per member and plan, enforced by a partial unique index (SPEC-0007 decision 2).
/// </summary>
internal sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public const string LiveIndex = "ux_subscriptions_live_subscriber_plan";

    private static readonly ValueConverter<SubscriptionStatus, string> StatusConversion =
        new(status => status.ToName(), name => SubscriptionStatusNames.Parse(name));

    public void Configure(EntityTypeBuilder<Subscription> subscription)
    {
        subscription.ToTable("subscriptions", table =>
        {
            table.HasCheckConstraint("ck_subscriptions_status", $"status IN ({string.Join(", ", SubscriptionStatusNames.All.Select(static n => $"'{n}'"))})");
            table.HasCheckConstraint("ck_subscriptions_amount", "amount > 0");
            table.HasCheckConstraint("ck_subscriptions_period", "period >= 0 AND failed_attempts >= 0");
            table.HasCheckConstraint("ck_subscriptions_cancel_reason", $"cancel_reason IS NULL OR cancel_reason IN ('{Subscription.CancelledOnRequest}', '{Subscription.CancelledForNonPayment}', '{Subscription.CancelledForRemovedMember}')");
        });
        subscription.HasKey(s => s.Id).HasName("pk_subscriptions");
        subscription.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
        subscription.Property(s => s.TenantId).HasColumnName("tenant_id");
        subscription.Property(s => s.PlanId).HasColumnName("plan_id");
        subscription.Property(s => s.SubscriberMembershipId).HasColumnName("subscriber_membership_id");
        subscription.Property(s => s.Status).HasColumnName("status").HasConversion(StatusConversion).HasMaxLength(32);
        subscription.Property(s => s.PaymentMethod).HasColumnName("payment_method").HasMaxLength(100).IsRequired();
        subscription.Property(s => s.Amount).HasColumnName("amount").HasPrecision(19, 4);
        subscription.Property(s => s.Currency).HasColumnName("currency").HasColumnType("character(3)").IsRequired();
        subscription.Property(s => s.Interval).HasColumnName("interval").HasConversion(PlanConfiguration.IntervalConversion).HasMaxLength(16);
        subscription.Property(s => s.AnchorAt).HasColumnName("anchor_at");
        subscription.Property(s => s.Period).HasColumnName("period");
        subscription.Property(s => s.PeriodEnd).HasColumnName("period_end");
        subscription.Property(s => s.FailedAttempts).HasColumnName("failed_attempts");
        subscription.Property(s => s.CancelAtPeriodEnd).HasColumnName("cancel_at_period_end");
        subscription.Property(s => s.CancelReason).HasColumnName("cancel_reason").HasMaxLength(32);
        subscription.Property(s => s.CreatedAt).HasColumnName("created_at");
        subscription.Property(s => s.UpdatedAt).HasColumnName("updated_at");
        subscription.HasIndex(s => new { s.TenantId, s.SubscriberMembershipId, s.PlanId })
            .IsUnique()
            .HasDatabaseName(LiveIndex)
            .HasFilter($"status <> '{SubscriptionStatusNames.Cancelled}'");
    }
}
