using System.Text.RegularExpressions;

namespace TenantFlow.Payments.Contracts;

/// <summary>
/// What a payment-method token may look like (SPEC-0006 criterion 15): letters, digits and underscores, 3 to 100
/// characters, and never a run of 12 or more digits, the shape of a card number, even split by underscores. Shared with
/// modules that store a token for later charges (SPEC-0007 criterion 2), so no module accepts card data.
/// </summary>
public static partial class PaymentMethodTokens
{
    public const int MaxLength = 100;

    public static bool IsValid(string? token) =>
        token is { Length: >= 3 and <= MaxLength } && TokenPattern().IsMatch(token) && !CardNumberPattern().IsMatch(token.Replace("_", string.Empty, StringComparison.Ordinal));

    [GeneratedRegex("^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TokenPattern();

    [GeneratedRegex("[0-9]{12,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CardNumberPattern();
}
