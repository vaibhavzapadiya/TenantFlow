namespace TenantFlow.Catalog.Contracts;

/// <summary>
/// Product imports for the ImportsExports module (SPEC-0008 criteria 4 and 5, ADR-0024 decision 2), in the tenant of the
/// current tenant context. A batch is applied exactly once per (job, batch number): asking again answers with the first
/// result and changes nothing, so a job that resumes after a crash never creates a product twice and its counts stay
/// exact.
/// </summary>
public interface ICatalogImport
{
    /// <summary>
    /// Creates or updates the products of <paramref name="rows"/> by SKU (ignoring case); rows that break a product rule
    /// are left out and reported by row, column and error code (never the value).
    /// </summary>
    Task<ImportBatchResult> ApplyBatchAsync(Guid jobId, int batchNumber, IReadOnlyList<ProductImportRow> rows, CancellationToken cancellationToken);
}

/// <summary>One CSV row as read: the values as text, so Catalog's own rules decide what is valid.</summary>
/// <param name="Row">The row number in the file, from 1 for the first data row.</param>
/// <param name="Sku">The <c>sku</c> column.</param>
/// <param name="Name">The <c>name</c> column.</param>
/// <param name="PriceAmount">The <c>price_amount</c> column, a decimal with <c>.</c>.</param>
/// <param name="PriceCurrency">The <c>price_currency</c> column.</param>
/// <param name="Status">The <c>status</c> column (<c>active</c> or <c>archived</c>).</param>
public sealed record ProductImportRow(int Row, string Sku, string Name, string PriceAmount, string PriceCurrency, string Status);

/// <summary>What a batch did.</summary>
public sealed record ImportBatchResult(int Created, int Updated, IReadOnlyList<ImportRowError> Errors);

/// <summary>A row that was not imported: where and why, never the value.</summary>
public sealed record ImportRowError(int Row, string Column, string Code);
