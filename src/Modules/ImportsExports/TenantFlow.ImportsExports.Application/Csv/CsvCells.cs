using System.Text;

namespace TenantFlow.ImportsExports.Application.Csv;

/// <summary>
/// CSV cells TenantFlow writes (SPEC-0008 criterion 8). Every cell is quoted, and text that a spreadsheet would evaluate,
/// one starting with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a carriage return, or the full-width <c>＝ ＋ － ＠</c>
/// that some spreadsheets read as the same, gets a leading <c>'</c> (OWASP CSV injection guidance). An import restores such a cell, so an exported file imports back to the same values.
/// </summary>
public static class CsvCells
{
    private static readonly char[] FormulaStarts = ['=', '+', '-', '@', '\t', '\r', '\uFF1D', '\uFF0B', '\uFF0D', '\uFF20'];

    /// <summary>Text with a leading <c>'</c> when it starts with a formula character.</summary>
    public static string Neutralize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length > 0 && FormulaStarts.Contains(text[0]) ? "'" + text : text;
    }

    /// <summary>The text an export neutralized: a leading <c>'</c> before a formula character is removed.</summary>
    public static string Restore(string cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        return cell.Length > 1 && cell[0] == '\'' && FormulaStarts.Contains(cell[1]) ? cell[1..] : cell;
    }

    /// <summary>Appends one CSV line: each cell quoted with its quotes doubled, then a CRLF line end.</summary>
    public static void AppendRow(StringBuilder line, params ReadOnlySpan<string> cells)
    {
        ArgumentNullException.ThrowIfNull(line);
        for (var i = 0; i < cells.Length; i++)
        {
            line.Append(i == 0 ? "\"" : ",\"").Append(cells[i].Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }

        line.Append("\r\n");
    }
}
