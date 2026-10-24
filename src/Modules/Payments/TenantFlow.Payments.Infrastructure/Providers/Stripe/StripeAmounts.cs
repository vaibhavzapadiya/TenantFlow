namespace TenantFlow.Payments.Infrastructure.Providers.Stripe;

/// <summary>
/// Stripe takes amounts in the currency's smallest unit (AGENTS.md rule 4, documented minor units): cents for EUR, whole
/// yen for JPY, and for three-decimal currencies a multiple of ten. ISK and UGX are Stripe's special cases (SPEC-0007
/// criterion 16): zero-decimal currencies that Stripe still takes in hundredths, so whole units times 100. An amount those
/// units cannot hold exactly is refused, never rounded: a payment must charge exactly the order's total.
/// </summary>
internal static class StripeAmounts
{
    // Stripe's zero-decimal and three-decimal currencies (lower case, as Stripe writes them).
    private static readonly HashSet<string> ZeroDecimal = new(StringComparer.Ordinal)
    {
        "bif", "clp", "djf", "gnf", "jpy", "kmf", "krw", "mga", "pyg", "rwf", "vnd", "vuv", "xaf", "xof", "xpf",
    };

    // Whole units only, sent as hundredths: an amount Stripe gets must be divisible by 100.
    private static readonly HashSet<string> ZeroDecimalSentAsTwoDecimal = new(StringComparer.Ordinal) { "isk", "ugx" };

    private static readonly HashSet<string> ThreeDecimal = new(StringComparer.Ordinal) { "bhd", "jod", "kwd", "omr", "tnd" };

    /// <summary>The amount in minor units, or null when it cannot be expressed exactly.</summary>
    public static long? ToMinorUnits(decimal amount, string currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        var code = currency.ToLowerInvariant();
        var (scale, step) = ZeroDecimal.Contains(code) ? (1m, 1m)
            : ThreeDecimal.Contains(code) ? (1000m, 10m)
            : ZeroDecimalSentAsTwoDecimal.Contains(code) ? (100m, 100m)
            : (100m, 1m);
        var minor = amount * scale;
        return minor == decimal.Truncate(minor) && minor % step == 0 && minor > 0 && minor <= long.MaxValue ? (long)minor : null;
    }
}
