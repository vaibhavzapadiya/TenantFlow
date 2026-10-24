using System.Globalization;
using System.Text;
using TenantFlow.Catalog.Contracts;
using TenantFlow.ImportsExports.Application.Csv;
using TenantFlow.ImportsExports.Domain.Jobs;

namespace TenantFlow.ImportsExports.Application.ProductExports;

/// <summary>
/// The products of the tenant as a CSV byte stream (SPEC-0008 criteria 7 and 8), read from Catalog a page at a time only as
/// storage reads it, so an export never holds more than one page in memory. Columns are the import's; text cells are
/// neutralized (<see cref="CsvCells"/>); UTF-8 without a byte-order mark. More than <paramref name="maxRows"/> products is
/// a <see cref="TooManyRowsException"/>, so storage discards what it wrote.
/// </summary>
public sealed class ProductCsvStream(ICatalogExport catalog, int maxRows, int pageSize) : Stream
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private byte[] _pending = HeaderBytes();
    private int _offset;
    private ProductExportCursor? _after;
    private bool _done;

    /// <summary>Products written so far.</summary>
    public int Rows { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_offset == _pending.Length)
        {
            if (_done)
            {
                return 0;
            }

            await NextPageAsync(cancellationToken);
        }

        var count = Math.Min(buffer.Length, _pending.Length - _offset);
        _pending.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    // Catalog is async only: a synchronous read would block on it (AGENTS.md rule 5).
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private static byte[] HeaderBytes()
    {
        var header = new StringBuilder();
        CsvCells.AppendRow(header, "sku", "name", "price_amount", "price_currency", "status");
        return Utf8.GetBytes(header.ToString());
    }

    private async Task NextPageAsync(CancellationToken cancellationToken)
    {
        var page = await catalog.ReadPageAsync(_after, pageSize, cancellationToken);
        if (Rows + page.Count > maxRows)
        {
            throw new TooManyRowsException();
        }

        var lines = new StringBuilder();
        foreach (var product in page)
        {
            CsvCells.AppendRow(lines, CsvCells.Neutralize(product.Sku), CsvCells.Neutralize(product.Name),
                product.PriceAmount.ToString(CultureInfo.InvariantCulture), product.PriceCurrency, product.Status);
        }

        Rows += page.Count;
        _done = page.Count < pageSize;
        _after = page.Count > 0 ? page[^1].Cursor : _after;
        (_pending, _offset) = (Utf8.GetBytes(lines.ToString()), 0);
    }
}

/// <summary>An export has more rows than allowed (<see cref="JobFailureCodes.TooManyRows"/>).</summary>
public sealed class TooManyRowsException : Exception
{
    public TooManyRowsException()
        : base("The export has more rows than allowed.")
    {
    }

    public TooManyRowsException(string message)
        : base(message)
    {
    }

    public TooManyRowsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
