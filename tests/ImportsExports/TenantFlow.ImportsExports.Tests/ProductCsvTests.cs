using System.Text;
using TenantFlow.Catalog.Contracts;
using TenantFlow.ImportsExports.Application.Csv;
using TenantFlow.ImportsExports.Application.ProductExports;

namespace TenantFlow.ImportsExports.Tests;

/// <summary>SPEC-0008 criteria 7 and 8: the export's CSV, its formula neutralizing, and its row limit.</summary>
public sealed class ProductCsvTests
{
    [Theory]
    [InlineData("=1+1")]
    [InlineData("+cmd")]
    [InlineData("-2")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tx")]
    [InlineData("\rx")]
    [InlineData("\uFF1DHYPERLINK(1)")]
    [InlineData("\uFF0Bx")]
    [InlineData("\uFF0Dx")]
    [InlineData("\uFF20x")]
    public void A_cell_that_starts_with_a_formula_character_is_neutralized_and_restored(string text)
    {
        Assert.Equal("'" + text, CsvCells.Neutralize(text));
        Assert.Equal(text, CsvCells.Restore(CsvCells.Neutralize(text)));
    }

    [Theory]
    [InlineData("Plate")]
    [InlineData("'quoted")]
    [InlineData("")]
    public void Other_text_is_left_alone(string text)
    {
        Assert.Equal(text, CsvCells.Neutralize(text));
        Assert.Equal(text, CsvCells.Restore(text));
    }

    [Fact]
    public async Task Every_product_is_written_in_pages_with_every_cell_quoted_and_reads_back()
    {
        var catalog = new FakeCatalog(5);
        await using var csv = new ProductCsvStream(catalog, maxRows: 10, pageSize: 2);

        var text = await ReadAllAsync(csv);
        var rows = new List<string[]>();
        await foreach (var row in CsvReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), TestContext.Current.CancellationToken))
        {
            rows.Add(row);
        }

        Assert.StartsWith("\"sku\",\"name\",\"price_amount\",\"price_currency\",\"status\"\r\n\"'=SKU-1\",\"Say \"\"hi\"\" 1\",\"1.50\",\"EUR\",\"active\"\r\n", text, StringComparison.Ordinal);
        Assert.Equal((5, 6, 3), (csv.Rows, rows.Count, catalog.Pages));
        Assert.Equal(["'=SKU-5", "Say \"hi\" 5", "1.50", "EUR", "active"], rows[^1]);
    }

    [Fact]
    public async Task More_rows_than_allowed_stops_the_stream()
    {
        await using var csv = new ProductCsvStream(new FakeCatalog(5), maxRows: 4, pageSize: 2);

        await Assert.ThrowsAsync<TooManyRowsException>(() => ReadAllAsync(csv));
    }

    private static async Task<string> ReadAllAsync(Stream csv)
    {
        using var reader = new StreamReader(csv, Encoding.UTF8);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    private sealed class FakeCatalog(int products) : ICatalogExport
    {
        public int Pages { get; private set; }

        public Task<IReadOnlyList<ProductExportRow>> ReadPageAsync(ProductExportCursor? after, int limit, CancellationToken cancellationToken)
        {
            Pages++;
            var start = after is null ? 1 : int.Parse(after.Sku[5..], System.Globalization.CultureInfo.InvariantCulture) + 1;
            IReadOnlyList<ProductExportRow> page = [.. Enumerable.Range(start, Math.Max(0, Math.Min(limit, products - start + 1)))
                .Select(static i => new ProductExportRow(Guid.CreateVersion7(), $"=SKU-{i}", $"Say \"hi\" {i}", 1.50m, "EUR", "active"))];
            return Task.FromResult(page);
        }
    }
}
