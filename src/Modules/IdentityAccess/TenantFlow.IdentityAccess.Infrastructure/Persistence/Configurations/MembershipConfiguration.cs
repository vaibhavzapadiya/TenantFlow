using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantFlow.BuildingBlocks.Abstractions.Identity;
using TenantFlow.IdentityAccess.Domain.Memberships;

namespace TenantFlow.IdentityAccess.Infrastructure.Persistence.Configurations;

internal sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> membership)
    {
        membership.ToTable("memberships", table =>
        {
            table.HasCheckConstraint("ck_memberships_role", "role IN ('tenant-admin', 'customer')");
            table.HasCheckConstraint("ck_memberships_status", "status IN ('active', 'removed')");
        });
        membership.HasKey(m => m.Id).HasName("pk_memberships");
        membership.Property(m => m.Id).HasColumnName("id").HasConversion(IdentityValueConversions.MembershipIdValue).ValueGeneratedNever();
        membership.Property(m => m.TenantId).HasColumnName("tenant_id").HasConversion(IdentityValueConversions.TenantIdValue);
        membership.Property(m => m.Issuer).HasColumnName("issuer").HasMaxLength(IdentityLimits.IssuerMaxLength).IsRequired();
        membership.Property(m => m.Subject).HasColumnName("subject").HasMaxLength(IdentityLimits.SubjectMaxLength).IsRequired();
        membership.Property(m => m.Role).HasColumnName("role").HasConversion(IdentityValueConversions.RoleRequired).HasMaxLength(16);
        membership.Property(m => m.Status).HasColumnName("status").HasConversion(IdentityValueConversions.StatusRequired).HasMaxLength(16);
        membership.Property(m => m.CreatedAt).HasColumnName("created_at");
        membership.Property(m => m.UpdatedAt).HasColumnName("updated_at");
        membership.Ignore(m => m.IsActive);

        // One active membership per identity per tenant; removed ones stay as history (criterion 19).
        membership.HasIndex(m => new { m.TenantId, m.Issuer, m.Subject })
            .IsUnique()
            .HasFilter("status = 'active'")
            .HasDatabaseName("ux_memberships_tenant_id_issuer_subject_active");
        // A membership is always read together with its tenant, so a wrong-tenant lookup cannot match (criterion 17).
        membership.HasIndex(m => new { m.TenantId, m.Id }).IsUnique().HasDatabaseName("ux_memberships_tenant_id_id");
        membership.HasIndex(m => new { m.Issuer, m.Subject }).HasDatabaseName("ix_memberships_issuer_subject");
    }
}
