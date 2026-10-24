using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TenantFlow.Subscriptions.Domain.Plans;

namespace TenantFlow.Subscriptions.Infrastructure.Persistence.Configurations;

/// <summary>The plan row. The interval is stored by its stable name, and the database refuses any other.</summary>
internal sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    internal static readonly ValueConverter<BillingInterval, string> IntervalConversion =
        new(interval => interval.ToName(), name => BillingIntervalNames.Parse(name));

    public void Configure(EntityTypeBuilder<Plan> plan)
    {
        plan.ToTable("plans", table =>
        {
            table.HasCheckConstraint("ck_plans_amount", "amount > 0");
            table.HasCheckConstraint("ck_plans_interval", $"interval IN ({string.Join(", ", BillingIntervalNames.All.Select(static n => $"'{n}'"))})");
            table.HasCheckConstraint("ck_plans_trial_days", $"trial_days BETWEEN 0 AND {Plan.MaxTrialDays}");
        });
        plan.HasKey(p => p.Id).HasName("pk_plans");
        plan.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
        plan.Property(p => p.TenantId).HasColumnName("tenant_id");
        plan.Property(p => p.Name).HasColumnName("name").HasMaxLength(Plan.MaxNameLength).IsRequired();
        plan.Property(p => p.Amount).HasColumnName("amount").HasPrecision(19, 4);
        plan.Property(p => p.Currency).HasColumnName("currency").HasColumnType("character(3)").IsRequired();
        plan.Property(p => p.Interval).HasColumnName("interval").HasConversion(IntervalConversion).HasMaxLength(16);
        plan.Property(p => p.TrialDays).HasColumnName("trial_days");
        plan.Property(p => p.Active).HasColumnName("active");
        plan.Property(p => p.CreatedAt).HasColumnName("created_at");
        plan.HasIndex(p => new { p.TenantId, p.CreatedAt }).HasDatabaseName("ix_plans_tenant_id_created_at");
    }
}
