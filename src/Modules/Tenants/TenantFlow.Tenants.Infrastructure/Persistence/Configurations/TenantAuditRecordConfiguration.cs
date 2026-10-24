using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantFlow.BuildingBlocks.Abstractions.Identity;
using TenantFlow.Tenants.Domain.Auditing;

namespace TenantFlow.Tenants.Infrastructure.Persistence.Configurations;

/// <summary>Append-only: the migration revokes UPDATE and DELETE on this table from the application role.</summary>
internal sealed class TenantAuditRecordConfiguration : IEntityTypeConfiguration<TenantAuditRecord>
{
    public void Configure(EntityTypeBuilder<TenantAuditRecord> audit)
    {
        audit.ToTable("tenant_audit");
        audit.HasKey(a => a.Id).HasName("pk_tenant_audit");
        audit.Property(a => a.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        audit.Property(a => a.TenantId).HasColumnName("tenant_id");
        audit.Property(a => a.Action).HasColumnName("action").HasMaxLength(64);
        audit.Property(a => a.ActorIssuer).HasColumnName("actor_issuer").HasMaxLength(IdentityLimits.IssuerMaxLength);
        audit.Property(a => a.ActorSubject).HasColumnName("actor_subject").HasMaxLength(IdentityLimits.SubjectMaxLength);
        audit.Property(a => a.OldStatus).HasColumnName("old_status").HasConversion(TenantStatusConversion.Optional).HasMaxLength(16);
        audit.Property(a => a.NewStatus).HasColumnName("new_status").HasConversion(TenantStatusConversion.Required).HasMaxLength(16);
        audit.Property(a => a.OccurredAt).HasColumnName("occurred_at");
        audit.HasIndex(a => new { a.TenantId, a.OccurredAt }).HasDatabaseName("ix_tenant_audit_tenant_id_occurred_at");
    }
}
