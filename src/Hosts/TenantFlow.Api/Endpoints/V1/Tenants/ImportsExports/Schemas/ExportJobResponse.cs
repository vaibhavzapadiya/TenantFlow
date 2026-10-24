using TenantFlow.ImportsExports.Application.ProductExports;

namespace TenantFlow.Api.Endpoints.V1.Tenants.ImportsExports.Schemas;

/// <summary>
/// An export job: status <c>queued</c>, <c>running</c>, <c>succeeded</c> or <c>failed</c> (with a failure code such as
/// <c>too_many_rows</c>), the rows written, and until when its file downloads.
/// </summary>
internal sealed record ExportJobResponse(Guid Id, string Status, string? FailureCode, int RowCount, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, DateTimeOffset? ExpiresAt)
{
    public static ExportJobResponse From(ExportJobDetails job) =>
        new(job.Id, job.Status, job.FailureCode, job.RowCount, job.CreatedAt, job.CompletedAt, job.ExpiresAt);
}
