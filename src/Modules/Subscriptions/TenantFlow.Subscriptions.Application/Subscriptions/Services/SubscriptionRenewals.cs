namespace TenantFlow.Subscriptions.Application.Subscriptions.Services;

/// <summary>The renewal schedule (ADR-0023, SPEC-0007 criteria 5 and 7).</summary>
public static class SubscriptionRenewals
{
    /// <summary>The due-row kind, in <c>subscriptions.workflow_timers</c>; due at the subscription's period end.</summary>
    public const string TimerKind = "subscriptions.renew";

    /// <summary>Failed charges for one period after which the subscription is cancelled.</summary>
    public const int MaxAttempts = 3;

    /// <summary>How long after a failed charge the next attempt is due.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromDays(1);

    /// <summary>How often a charge that is not settled yet is asked about again.</summary>
    public static readonly TimeSpan PendingCheckAfter = TimeSpan.FromMinutes(5);

    /// <summary>How often a charge in review is asked about again (as Payments checks it).</summary>
    public static readonly TimeSpan InReviewCheckAfter = TimeSpan.FromMinutes(10);

    /// <summary>How long renewals of a suspended tenant wait before it is asked about again (review fix R11).</summary>
    public static readonly TimeSpan SuspendedCheckAfter = TimeSpan.FromHours(1);

    /// <summary>The audit reason of the refund of a charge whose subscription ended before it settled (review fix R1).</summary>
    public const string AbandonedChargeReason = "Subscription ended before its renewal charge settled";
}
