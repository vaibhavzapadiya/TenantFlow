using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantFlow.BuildingBlocks.Abstractions.Identity;
using TenantFlow.IdentityAccess.Domain.Auditing;

namespace TenantFlow.IdentityAccess.Infrastructure.Persistence.Configurations;

/// <summary>Append-only: the migration revokes UPDATE, DELETE and TRUNCATE on this table from the application role.</summary>
internal sealed class MembershipAuditRecordConfiguration : IEntityTypeConfiguration<MembershipAuditRecord>
{
    public void Configure(EntityTypeBuilder<MembershipAuditRecord> audit)
    {
        audit.ToTable("membership_audit");
        audit.HasKey(a => a.Id).HasName("pk_membership_audit");
        audit.Property(a => a.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        audit.Property(a => a.TenantId).HasColumnName("tenant_id").HasConversion(IdentityValueConversions.TenantIdValue);
        audit.Property(a => a.MembershipId).HasColumnName("membership_id").HasConversion(IdentityValueConversions.MembershipIdValue);
        audit.Property(a => a.Action).HasColumnName("action").HasMaxLength(64);
        audit.Property(a => a.ActorIssuer).HasColumnName("actor_issuer").HasMaxLength(IdentityLimits.IssuerMaxLength);
        audit.Property(a => a.ActorSubject).HasColumnName("actor_subject").HasMaxLength(IdentityLimits.SubjectMaxLength);
        audit.Property(a => a.OldRole).HasColumnName("old_role").HasConversion(IdentityValueConversions.RoleOptional).HasMaxLength(16);
        audit.Property(a => a.NewRole).HasColumnName("new_role").HasConversion(IdentityValueConversions.RoleOptional).HasMaxLength(16);
        audit.Property(a => a.OccurredAt).HasColumnName("occurred_at");
        audit.HasIndex(a => new { a.TenantId, a.OccurredAt }).HasDatabaseName("ix_membership_audit_tenant_id_occurred_at");
    }
}
