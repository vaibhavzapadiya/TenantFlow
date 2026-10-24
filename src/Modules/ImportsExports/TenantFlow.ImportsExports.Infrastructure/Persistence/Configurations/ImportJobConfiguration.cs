using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TenantFlow.ImportsExports.Domain.Jobs;

namespace TenantFlow.ImportsExports.Infrastructure.Persistence.Configurations;

/// <summary>
/// The import job row (SPEC-0008 criteria 3 to 6): one per idempotency key of a tenant; its file referenced by id.
/// </summary>
internal sealed class ImportJobConfiguration : IEntityTypeConfiguration<ImportJob>
{
    internal static readonly ValueConverter<JobStatus, string> StatusConversion = new(s => s.ToName(), n => JobStatusNames.Parse(n));

    public void Configure(EntityTypeBuilder<ImportJob> job)
    {
        job.ToTable("import_jobs", table =>
        {
            table.HasCheckConstraint("ck_import_jobs_status", $"status IN ({string.Join(", ", JobStatusNames.All.Select(static n => $"'{n}'"))})");
            table.HasCheckConstraint("ck_import_jobs_counts", "checkpoint_row >= 0 AND created_count >= 0 AND updated_count >= 0 AND failed_count >= 0");
        });
        job.HasKey(j => j.Id).HasName("pk_import_jobs");
        job.Property(j => j.Id).HasColumnName("id").ValueGeneratedNever();
        job.Property(j => j.TenantId).HasColumnName("tenant_id");
        job.Property(j => j.Status).HasColumnName("status").HasConversion(StatusConversion).HasMaxLength(16);
        job.Property(j => j.FileId).HasColumnName("file_id");
        job.Property(j => j.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(ImportJob.MaxIdempotencyKeyLength).IsRequired();
        job.Property(j => j.RequestHash).HasColumnName("request_hash").HasColumnType("character(64)").IsRequired();
        job.Property(j => j.CheckpointRow).HasColumnName("checkpoint_row");
        job.Property(j => j.CreatedCount).HasColumnName("created_count");
        job.Property(j => j.UpdatedCount).HasColumnName("updated_count");
        job.Property(j => j.FailedCount).HasColumnName("failed_count");
        job.Property(j => j.FailureCode).HasColumnName("failure_code").HasMaxLength(64);
        job.Property(j => j.RequestedBy).HasColumnName("requested_by");
        job.Property(j => j.CreatedAt).HasColumnName("created_at");
        job.Property(j => j.UpdatedAt).HasColumnName("updated_at");
        job.Property(j => j.CompletedAt).HasColumnName("completed_at");
        job.HasIndex(j => new { j.TenantId, j.IdempotencyKey }).IsUnique().HasDatabaseName("ux_import_jobs_tenant_id_idempotency_key");
        job.HasIndex(j => new { j.TenantId, j.Status }).HasDatabaseName("ix_import_jobs_tenant_id_status");
    }
}

/// <summary>The row-error row: one per (job, row, column).</summary>
internal sealed class ImportRowErrorConfiguration : IEntityTypeConfiguration<ImportRowErrorRow>
{
    public void Configure(EntityTypeBuilder<ImportRowErrorRow> error)
    {
        error.ToTable("import_row_errors");
        error.HasKey(e => new { e.TenantId, e.JobId, e.Row, e.Column }).HasName("pk_import_row_errors");
        error.Property(e => e.TenantId).HasColumnName("tenant_id");
        error.Property(e => e.JobId).HasColumnName("job_id");
        error.Property(e => e.Row).HasColumnName("row");
        error.Property(e => e.Column).HasColumnName("column").HasMaxLength(32);
        error.Property(e => e.Code).HasColumnName("code").HasMaxLength(64);
    }
}
