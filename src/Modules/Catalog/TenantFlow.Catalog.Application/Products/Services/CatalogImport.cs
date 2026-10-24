using System.Globalization;
using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.Catalog.Application.Abstractions;
using TenantFlow.Catalog.Contracts;
using TenantFlow.Catalog.Domain.Products;

namespace TenantFlow.Catalog.Application.Products.Services;

/// <summary>
/// Applies a batch of imported product rows (SPEC-0008 criteria 4 and 5): each row is checked with the product's own rules
/// (the same as the API's), invalid ones are reported by column and code, and the valid ones are created or updated by
/// SKU in one transaction together with the batch's record, so a repeated batch changes nothing and answers the same.
/// </summary>
public sealed class CatalogImport(ICatalogStore store, TimeProvider clock) : ICatalogImport
{
    public async Task<ImportBatchResult> ApplyBatchAsync(Guid jobId, int batchNumber, IReadOnlyList<ProductImportRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var errors = new List<ImportRowError>();
        var valid = new List<ProductImport>();
        foreach (var row in rows)
        {
            decimal? amount = decimal.TryParse(row.PriceAmount, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            var rowErrors = new (string Column, AppError? Error)[]
            {
                ("sku", Product.CheckSku(row.Sku)),
                ("name", Product.CheckName(row.Name)),
                ("price_amount", Product.CheckPriceAmount(amount)),
                ("price_currency", Product.CheckPriceCurrency(row.PriceCurrency)),
                ("status", Product.CheckStatus(row.Status)),
            }.Where(static e => e.Error is not null).Select(e => new ImportRowError(row.Row, e.Column, e.Error!.Code)).ToList();
            if (rowErrors.Count > 0)
            {
                errors.AddRange(rowErrors);
            }
            else
            {
                valid.Add(new ProductImport(row.Sku.Trim(), row.Name, amount!.Value, row.PriceCurrency, row.Status));
            }
        }

        return await store.ApplyImportBatchAsync(jobId, batchNumber, valid, errors, clock.GetUtcNow(), cancellationToken);
    }
}
