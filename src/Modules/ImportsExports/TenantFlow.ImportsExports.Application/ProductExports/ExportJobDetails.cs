using TenantFlow.ImportsExports.Domain.Jobs;

namespace TenantFlow.ImportsExports.Application.ProductExports;

/// <summary>An export job as callers see it.</summary>
public sealed record ExportJobDetails(Guid Id, string Status, string? FailureCode, int RowCount, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, DateTimeOffset? ExpiresAt)
{
    public static ExportJobDetails From(ExportJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new(job.Id, job.Status.ToName(), job.FailureCode, job.RowCount, job.CreatedAt, job.CompletedAt, job.ExpiresAt);
    }
}
