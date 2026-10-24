using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantFlow.Operations.Domain.SupportGrants;

namespace TenantFlow.Operations.Infrastructure.Persistence.Configurations;

/// <summary>
/// The support grant row (ADR-0027). Platform-owned like <c>tenants.tenants</c>, so not under row-level security: the
/// tenant id names whom the grant is for, and every read filters by it. No foreign key crosses into another schema.
/// </summary>
internal sealed class SupportGrantConfiguration : IEntityTypeConfiguration<SupportGrant>
{
    public void Configure(EntityTypeBuilder<SupportGrant> grant)
    {
        grant.ToTable("support_grants", table => table.HasCheckConstraint("ck_support_grants_window", "expires_at > starts_at"));
        grant.HasKey(g => g.Id).HasName("pk_support_grants");
        grant.Property(g => g.Id).HasColumnName("id").ValueGeneratedNever();
        grant.Property(g => g.TenantId).HasColumnName("tenant_id");
        grant.Property(g => g.OperatorRef).HasColumnName("operator_ref").HasMaxLength(64);
        grant.Property(g => g.Ticket).HasColumnName("ticket").HasMaxLength(SupportGrant.TicketMaxLength);
        grant.Property(g => g.StartsAt).HasColumnName("starts_at");
        grant.Property(g => g.ExpiresAt).HasColumnName("expires_at");
        grant.Property(g => g.EndedAt).HasColumnName("ended_at");
        grant.Property(g => g.EndedBy).HasColumnName("ended_by").HasMaxLength(64);
        grant.HasIndex(g => new { g.OperatorRef, g.TenantId, g.ExpiresAt }).HasDatabaseName("ix_support_grants_operator_ref_tenant_id_expires_at");
    }
}
