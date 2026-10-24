using System.Text;

namespace TenantFlow.Catalog.Application.Products.Services;

/// <summary>
/// Turns what a user typed into a PostgreSQL full-text query (SPEC-0008 criterion 1): every word must match, the last one
/// as a prefix when it has at least <see cref="MinPrefixLength"/> characters, so "blue mu" finds "Blue Mug"; a one-character
/// prefix would match a large share of every tenant's index entries before the tenant filter. Only letters and digits are kept, so nothing a user types can be read as a
/// query operator. The text configuration is <c>simple</c>: no stemming, the same for every language.
/// </summary>
public static class ProductSearch
{
    public const int MaxLength = 100;

    public const int MinPrefixLength = 2;

    /// <summary>The <c>to_tsquery</c> text, or null when the input holds no letter or digit (nothing can match).</summary>
    public static string? ToQuery(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var words = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            words.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        var terms = words.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return terms.Length == 0 ? null : string.Join(" & ", terms) + (terms[^1].Length >= MinPrefixLength ? ":*" : string.Empty);
    }
}
