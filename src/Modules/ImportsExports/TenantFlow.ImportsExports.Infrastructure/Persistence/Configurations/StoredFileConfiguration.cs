using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantFlow.ImportsExports.Domain.Files;

namespace TenantFlow.ImportsExports.Infrastructure.Persistence.Configurations;

/// <summary>The stored-file row (SPEC-0008 criterion 2): one per object, its key unique.</summary>
internal sealed class StoredFileConfiguration : IEntityTypeConfiguration<StoredFile>
{
    public void Configure(EntityTypeBuilder<StoredFile> file)
    {
        file.ToTable("stored_files", table =>
        {
            table.HasCheckConstraint("ck_stored_files_kind", $"kind IN ({string.Join(", ", FileKinds.All.Select(static k => $"'{k}'"))})");
            table.HasCheckConstraint("ck_stored_files_size", "size IS NULL OR size >= 0");
        });
        file.HasKey(f => f.Id).HasName("pk_stored_files");
        file.Property(f => f.Id).HasColumnName("id").ValueGeneratedNever();
        file.Property(f => f.TenantId).HasColumnName("tenant_id");
        file.Property(f => f.Kind).HasColumnName("kind").HasMaxLength(32).IsRequired();
        file.Property(f => f.Key).HasColumnName("key").HasMaxLength(128).IsRequired();
        file.Property(f => f.ContentType).HasColumnName("content_type").HasMaxLength(100).IsRequired();
        file.Property(f => f.Size).HasColumnName("size");
        file.Property(f => f.Sha256).HasColumnName("sha256").HasColumnType("character(64)");
        file.Property(f => f.CreatedAt).HasColumnName("created_at");
        file.Property(f => f.ExpiresAt).HasColumnName("expires_at");
        file.Property(f => f.DeletedAt).HasColumnName("deleted_at");
        file.HasIndex(f => f.Key).IsUnique().HasDatabaseName("ux_stored_files_key");
    }
}
