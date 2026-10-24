using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantFlow.ImportsExports.Domain.Jobs;

namespace TenantFlow.ImportsExports.Infrastructure.Persistence.Configurations;

/// <summary>The export job row (SPEC-0008 criterion 7): one per idempotency key of a tenant; its file referenced by id.</summary>
internal sealed class ExportJobConfiguration : IEntityTypeConfiguration<ExportJob>
{
    public void Configure(EntityTypeBuilder<ExportJob> job)
    {
        job.ToTable("export_jobs", table =>
        {
            table.HasCheckConstraint("ck_export_jobs_status", $"status IN ({string.Join(", ", JobStatusNames.All.Select(static n => $"'{n}'"))})");
            table.HasCheckConstraint("ck_export_jobs_row_count", "row_count >= 0");
        });
        job.HasKey(j => j.Id).HasName("pk_export_jobs");
        job.Property(j => j.Id).HasColumnName("id").ValueGeneratedNever();
        job.Property(j => j.TenantId).HasColumnName("tenant_id");
        job.Property(j => j.Status).HasColumnName("status").HasConversion(ImportJobConfiguration.StatusConversion).HasMaxLength(16);
        job.Property(j => j.FileId).HasColumnName("file_id");
        job.Property(j => j.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(ImportJob.MaxIdempotencyKeyLength).IsRequired();
        job.Property(j => j.RequestHash).HasColumnName("request_hash").HasColumnType("character(64)").IsRequired();
        job.Property(j => j.RowCount).HasColumnName("row_count");
        job.Property(j => j.FailureCode).HasColumnName("failure_code").HasMaxLength(64);
        job.Property(j => j.RequestedBy).HasColumnName("requested_by");
        job.Property(j => j.CreatedAt).HasColumnName("created_at");
        job.Property(j => j.UpdatedAt).HasColumnName("updated_at");
        job.Property(j => j.CompletedAt).HasColumnName("completed_at");
        job.Property(j => j.ExpiresAt).HasColumnName("expires_at");
        job.HasIndex(j => new { j.TenantId, j.IdempotencyKey }).IsUnique().HasDatabaseName("ux_export_jobs_tenant_id_idempotency_key");
        job.HasIndex(j => new { j.TenantId, j.Status }).HasDatabaseName("ix_export_jobs_tenant_id_status");
    }
}
