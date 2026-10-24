using TenantFlow.ImportsExports.Domain.Jobs;

namespace TenantFlow.ImportsExports.Application.ProductImports;

/// <summary>An import job as callers see it.</summary>
public sealed record ImportJobDetails(Guid Id, string Status, string? FailureCode, int Created, int Updated, int Failed, int RowsProcessed, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt)
{
    public static ImportJobDetails From(ImportJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new(job.Id, job.Status.ToName(), job.FailureCode, job.CreatedCount, job.UpdatedCount, job.FailedCount, job.CheckpointRow, job.CreatedAt, job.CompletedAt);
    }
}
