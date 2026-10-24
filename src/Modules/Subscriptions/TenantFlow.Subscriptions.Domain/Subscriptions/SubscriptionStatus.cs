namespace TenantFlow.Subscriptions.Domain.Subscriptions;

/// <summary>Where a subscription is (SPEC-0007, ADR-0023 decision 2). <see cref="SubscriptionStateMachine"/> says how it moves.</summary>
public enum SubscriptionStatus
{
    /// <summary>In its free trial; the first charge is due when the trial ends.</summary>
    Trialing = 1,

    /// <summary>Started without a trial; waiting for its first charge to succeed.</summary>
    Incomplete = 2,

    /// <summary>The current period is paid.</summary>
    Active = 3,

    /// <summary>A renewal charge failed; it is retried.</summary>
    PastDue = 4,

    /// <summary>Ended: never charged again. Final.</summary>
    Cancelled = 5,
}

/// <summary>The stable names a <see cref="SubscriptionStatus"/> has in the API and the database.</summary>
public static class SubscriptionStatusNames
{
    public const string Trialing = "trialing";
    public const string Incomplete = "incomplete";
    public const string Active = "active";
    public const string PastDue = "past_due";
    public const string Cancelled = "cancelled";

    /// <summary>Every name, for the database CHECK constraint.</summary>
    public static readonly IReadOnlyList<string> All = [Trialing, Incomplete, Active, PastDue, Cancelled];

    /// <exception cref="InvalidOperationException">The status is not one the domain defines.</exception>
    public static string ToName(this SubscriptionStatus status) => status switch
    {
        SubscriptionStatus.Trialing => Trialing,
        SubscriptionStatus.Incomplete => Incomplete,
        SubscriptionStatus.Active => Active,
        SubscriptionStatus.PastDue => PastDue,
        SubscriptionStatus.Cancelled => Cancelled,
        _ => throw new InvalidOperationException("The subscription status is not one the domain defines."),
    };

    /// <exception cref="InvalidOperationException">The stored name is not a known status name (case matters).</exception>
    public static SubscriptionStatus Parse(string name) => name switch
    {
        Trialing => SubscriptionStatus.Trialing,
        Incomplete => SubscriptionStatus.Incomplete,
        Active => SubscriptionStatus.Active,
        PastDue => SubscriptionStatus.PastDue,
        Cancelled => SubscriptionStatus.Cancelled,
        _ => throw new InvalidOperationException("The stored subscription status is not a known status name."),
    };
}
