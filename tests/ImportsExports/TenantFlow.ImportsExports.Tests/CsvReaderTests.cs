using System.Text;
using TenantFlow.ImportsExports.Application.Csv;

namespace TenantFlow.ImportsExports.Tests;

/// <summary>SPEC-0008 criterion 4: RFC 4180 reading, and bounded fields.</summary>
public sealed class CsvReaderTests
{
    [Fact]
    public async Task Quoted_fields_doubled_quotes_and_both_line_ends_are_read()
    {
        var rows = await ReadAsync("﻿a,\"b,c\",\"say \"\"hi\"\"\"\r\n1,,\"multi\nline\"\nlast,row,x");

        Assert.Equal([["a", "b,c", "say \"hi\""], ["1", "", "multi\nline"], ["last", "row", "x"]], rows);
    }

    [Fact]
    public async Task An_unclosed_quote_or_a_field_too_long_is_refused()
    {
        await Assert.ThrowsAsync<FormatException>(() => ReadAsync("a,\"open\n"));
        await Assert.ThrowsAsync<FormatException>(() => ReadAsync(new string('x', CsvReader.MaxFieldLength + 1)));
    }

    [Fact]
    public async Task Text_that_is_not_utf8_is_refused_rather_than_guessed()
    {
        // "Café" as a spreadsheet's Windows-1252 save writes it, and UTF-16 with its byte-order mark.
        await Assert.ThrowsAsync<FormatException>(() => ReadBytesAsync([.. "sku,name\nA,Caf"u8, 0xE9, (byte)'\n']));
        await Assert.ThrowsAsync<FormatException>(() => ReadBytesAsync([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("sku,name\n")]));
    }

    [Fact]
    public async Task A_row_may_have_at_most_32_fields()
    {
        Assert.Single(await ReadAsync(string.Join(',', Enumerable.Repeat("a", CsvReader.MaxFieldsPerRow))));
        await Assert.ThrowsAsync<FormatException>(() => ReadAsync(string.Join(',', Enumerable.Repeat("a", CsvReader.MaxFieldsPerRow + 1))));
    }

    [Fact]
    public async Task Blank_lines_are_skipped_but_an_empty_quoted_field_is_a_row()
    {
        var rows = await ReadAsync("a,b\r\n\r\n\nc,d\n\"\"\n\r\n");

        Assert.Equal([["a", "b"], ["c", "d"], [""]], rows);
    }

    private static Task<List<string[]>> ReadAsync(string text) => ReadBytesAsync(Encoding.UTF8.GetBytes(text));

    private static async Task<List<string[]>> ReadBytesAsync(byte[] bytes)
    {
        var rows = new List<string[]>();
        await foreach (var row in CsvReader.ReadAsync(new MemoryStream(bytes), TestContext.Current.CancellationToken))
        {
            rows.Add(row);
        }

        return rows;
    }
}
