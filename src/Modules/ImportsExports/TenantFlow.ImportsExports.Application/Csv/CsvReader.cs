using System.Text;

namespace TenantFlow.ImportsExports.Application.Csv;

/// <summary>
/// Reads RFC 4180 CSV: comma-separated fields, double-quoted fields with doubled quotes, CRLF or LF line ends. The text
/// must be UTF-8 (a UTF-8 byte-order mark is skipped; any other encoding, such as a spreadsheet's ANSI code page or UTF-16,
/// is an error rather than a guess). A field longer than <see cref="MaxFieldLength"/> or a row of more than
/// <see cref="MaxFieldsPerRow"/> fields is an error, so a hostile file cannot make one row take unbounded memory. Blank
/// lines are skipped.
/// </summary>
public static class CsvReader
{
    public const int MaxFieldLength = 1000;

    public const int MaxFieldsPerRow = 32;

    // Its preamble is the UTF-8 byte-order mark, which the reader skips; it never guesses another encoding.
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);

    /// <exception cref="FormatException">
    /// Text that is not UTF-8, an unterminated quote, text after a closing quote, a field too long, or too many fields.
    /// </exception>
    public static async IAsyncEnumerable<string[]> ReadAsync(Stream content, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(content, Utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var fields = new List<string>();
        var field = new StringBuilder();
        var (quoted, afterQuote, sawQuote) = (false, false, false);
        var buffer = new char[8192];
        int read;
        while ((read = await ReadChunkAsync(reader, buffer, cancellationToken)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        quoted = false;
                        afterQuote = true;
                    }
                    else
                    {
                        Append(field, c);
                    }

                    continue;
                }

                switch (c)
                {
                    case '"' when afterQuote:
                        // A doubled quote inside a quoted field.
                        Append(field, '"');
                        quoted = true;
                        afterQuote = false;
                        break;
                    case '"' when field.Length == 0:
                        quoted = true;
                        sawQuote = true;
                        break;
                    case ',':
                        if (fields.Count + 1 >= MaxFieldsPerRow)
                        {
                            throw new FormatException("A row has more fields than allowed.");
                        }

                        fields.Add(field.ToString());
                        field.Clear();
                        afterQuote = false;
                        break;
                    case '\r':
                        break;
                    case '\n':
                        if (!IsBlank(fields, field, sawQuote))
                        {
                            fields.Add(field.ToString());
                            yield return [.. fields];
                        }

                        fields.Clear();
                        field.Clear();
                        (afterQuote, sawQuote) = (false, false);
                        break;
                    default:
                        if (afterQuote || c == '"')
                        {
                            throw new FormatException("Unexpected text around a quoted field.");
                        }

                        Append(field, c);
                        break;
                }
            }
        }

        if (quoted)
        {
            throw new FormatException("A quoted field is not closed.");
        }

        if (!IsBlank(fields, field, sawQuote))
        {
            fields.Add(field.ToString());
            yield return [.. fields];
        }
    }

    // A line with nothing on it; a line with "" on it is one empty field, not blank.
    private static bool IsBlank(List<string> fields, StringBuilder field, bool sawQuote) => fields.Count == 0 && field.Length == 0 && !sawQuote;

    private static async Task<int> ReadChunkAsync(StreamReader reader, char[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadAsync(buffer, cancellationToken);
        }
        catch (DecoderFallbackException exception)
        {
            throw new FormatException("The file is not UTF-8 text.", exception);
        }
    }

    private static void Append(StringBuilder field, char c)
    {
        if (field.Length >= MaxFieldLength)
        {
            throw new FormatException("A field is longer than allowed.");
        }

        field.Append(c);
    }
}
