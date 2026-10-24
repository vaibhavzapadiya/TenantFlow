using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantFlow.Tenants.Domain.Tenants;

namespace TenantFlow.Tenants.Infrastructure.Persistence.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> tenant)
    {
        tenant.ToTable("tenants", table =>
            table.HasCheckConstraint("ck_tenants_status", "status IN ('active', 'suspended')"));
        tenant.HasKey(t => t.Id).HasName("pk_tenants");
        tenant.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
        tenant.Property(t => t.Name).HasColumnName("name").HasMaxLength(Tenant.NameMaxLength).IsRequired();
        tenant.Property(t => t.Status).HasColumnName("status").HasConversion(TenantStatusConversion.Required).HasMaxLength(16);
        tenant.Property(t => t.CreatedAt).HasColumnName("created_at");
        tenant.Property(t => t.UpdatedAt).HasColumnName("updated_at");
        // Optimistic concurrency on PostgreSQL's row version: concurrent status changes cannot both win.
        tenant.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
    }
}
