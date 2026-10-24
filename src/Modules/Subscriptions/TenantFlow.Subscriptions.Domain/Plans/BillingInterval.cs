namespace TenantFlow.Subscriptions.Domain.Plans;

/// <summary>How often a plan is charged (SPEC-0007 criterion 1).</summary>
public enum BillingInterval
{
    Month = 1,
    Year = 2,
}

/// <summary>The stable names a <see cref="BillingInterval"/> has in the API and the database.</summary>
public static class BillingIntervalNames
{
    public const string Month = "month";
    public const string Year = "year";

    /// <summary>Every name, for the database CHECK constraint.</summary>
    public static readonly IReadOnlyList<string> All = [Month, Year];

    /// <exception cref="InvalidOperationException">The interval is not one the domain defines.</exception>
    public static string ToName(this BillingInterval interval) => interval switch
    {
        BillingInterval.Month => Month,
        BillingInterval.Year => Year,
        _ => throw new InvalidOperationException("The billing interval is not one the domain defines."),
    };

    public static bool TryParse(string? name, out BillingInterval interval)
    {
        (var known, interval) = name switch
        {
            Month => (true, BillingInterval.Month),
            Year => (true, BillingInterval.Year),
            _ => (false, default),
        };
        return known;
    }

    /// <exception cref="InvalidOperationException">The stored name is not a known interval name (case matters).</exception>
    public static BillingInterval Parse(string name) =>
        TryParse(name, out var interval) ? interval : throw new InvalidOperationException("The stored billing interval is not a known name.");

    /// <summary>
    /// The end of billing period <paramref name="period"/> counted from <paramref name="anchor"/>: always from the anchor,
    /// never from the previous end, so a month end does not drift (Jan 31 -> Feb 28 -> Mar 31; SPEC-0007 criterion 5).
    /// </summary>
    public static DateTimeOffset PeriodEnd(this BillingInterval interval, DateTimeOffset anchor, int period) => interval switch
    {
        BillingInterval.Month => anchor.AddMonths(period),
        BillingInterval.Year => anchor.AddYears(period),
        _ => throw new InvalidOperationException("The billing interval is not one the domain defines."),
    };
}
