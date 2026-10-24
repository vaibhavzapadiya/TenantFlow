using TenantFlow.ImportsExports.Application.ProductImports;

namespace TenantFlow.Api.Endpoints.V1.Tenants.ImportsExports.Schemas;

/// <summary>
/// An import job: status <c>receiving</c>, <c>queued</c>, <c>running</c>, <c>succeeded</c> or <c>failed</c> (with a failure
/// code such as <c>invalid_header</c> or <c>too_many_rows</c>), and what it did so far.
/// </summary>
internal sealed record ImportJobResponse(Guid Id, string Status, string? FailureCode, int Created, int Updated, int Failed, int RowsProcessed, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt)
{
    public static ImportJobResponse From(ImportJobDetails job) =>
        new(job.Id, job.Status, job.FailureCode, job.Created, job.Updated, job.Failed, job.RowsProcessed, job.CreatedAt, job.CompletedAt);
}

/// <summary>A row that was not imported: its number (from 1 after the header), the column, and the error code.</summary>
internal sealed record ImportErrorResponse(int Row, string Column, string Code);

/// <summary>The row errors of an import, by row (the first 1,000).</summary>
internal sealed record ImportErrorsResponse(IReadOnlyList<ImportErrorResponse> Items);
