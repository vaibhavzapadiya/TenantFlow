namespace TenantFlow.Subscriptions.Domain.Subscriptions;

/// <summary>What can happen to a subscription (SPEC-0007 criteria 3, 5 and 7).</summary>
public enum SubscriptionTrigger
{
    /// <summary>The subscriber or a manager asks to cancel at the end of the paid or trial period.</summary>
    CancelAtPeriodEnd = 1,

    /// <summary>The subscriber or a manager cancels a subscription that has no paid period to run out.</summary>
    CancelNow = 2,

    /// <summary>The period of a subscription marked to cancel ended.</summary>
    PeriodEnded = 3,

    /// <summary>The charge for the next period succeeded.</summary>
    ChargeSucceeded = 4,

    /// <summary>The charge for the next period failed and attempts remain.</summary>
    ChargeFailed = 5,

    /// <summary>The last allowed charge attempt failed.</summary>
    AttemptsExhausted = 6,

    /// <summary>The charge for the next period is in review: its outcome may stay open for long (review fix R5).</summary>
    ChargeInReview = 7,

    /// <summary>The subscriber's membership was removed (review fix R9).</summary>
    MemberRemoved = 8,
}

/// <summary>
/// The subscription's state table (SPEC-0007, ADR-0023 decision 2): the one place that says which trigger is allowed in
/// which state and where it leads. Anything not listed is an illegal transition. The store applies a transition with a
/// conditional update on the expected state, so two racing changes cannot both win.
/// </summary>
public static class SubscriptionStateMachine
{
    private static readonly Dictionary<(SubscriptionStatus From, SubscriptionTrigger Trigger), SubscriptionStatus> Transitions = new()
    {
        [(SubscriptionStatus.Trialing, SubscriptionTrigger.CancelAtPeriodEnd)] = SubscriptionStatus.Trialing,
        [(SubscriptionStatus.Active, SubscriptionTrigger.CancelAtPeriodEnd)] = SubscriptionStatus.Active,
        [(SubscriptionStatus.Incomplete, SubscriptionTrigger.CancelNow)] = SubscriptionStatus.Cancelled,
        [(SubscriptionStatus.PastDue, SubscriptionTrigger.CancelNow)] = SubscriptionStatus.Cancelled,
        [(SubscriptionStatus.Trialing, SubscriptionTrigger.PeriodEnded)] = SubscriptionStatus.Cancelled,
        [(SubscriptionStatus.Active, SubscriptionTrigger.PeriodEnded)] = SubscriptionStatus.Cancelled,
        [(SubscriptionStatus.Trialing, SubscriptionTrigger.ChargeSucceeded)] = SubscriptionStatus.Active,
        [(SubscriptionStatus.Incomplete, SubscriptionTrigger.ChargeSucceeded)] = SubscriptionStatus.Active,
        [(SubscriptionStatus.Active, SubscriptionTrigger.ChargeSucceeded)] = SubscriptionStatus.Active,
        [(SubscriptionStatus.PastDue, SubscriptionTrigger.ChargeSucceeded)] = SubscriptionStatus.Active,
        [(SubscriptionStatus.Trialing, SubscriptionTrigger.ChargeFailed)] = SubscriptionStatus.PastDue,
        [(SubscriptionStatus.Active, SubscriptionTrigger.ChargeFailed)] = SubscriptionStatus.PastDue,
        [(SubscriptionStatus.PastDue, SubscriptionTrigger.ChargeFailed)] = SubscriptionStatus.PastDue,
        // A first charge that fails ends a subscription that never started (SPEC-0007 criterion 7).
        [(SubscriptionStatus.Incomplete, SubscriptionTrigger.ChargeFailed)] = SubscriptionStatus.Cancelled,
        [(SubscriptionStatus.PastDue, SubscriptionTrigger.AttemptsExhausted)] = SubscriptionStatus.Cancelled,
        // A charge in review is not paid: the time it covers is not granted while it waits.
        [(SubscriptionStatus.Trialing, SubscriptionTrigger.ChargeInReview)] = SubscriptionStatus.PastDue,
        [(SubscriptionStatus.Active, SubscriptionTrigger.ChargeInReview)] = SubscriptionStatus.PastDue,
        [(SubscriptionStatus.PastDue, SubscriptionTrigger.ChargeInReview)] = SubscriptionStatus.PastDue,
        [(SubscriptionStatus.Incomplete, SubscriptionTrigger.ChargeInReview)] = SubscriptionStatus.Incomplete,
        [(SubscriptionStatus.Trialing, SubscriptionTrigger.MemberRemoved)] = SubscriptionStatus.Cancelled,
        [(SubscriptionStatus.Incomplete, SubscriptionTrigger.MemberRemoved)] = SubscriptionStatus.Cancelled,
        [(SubscriptionStatus.Active, SubscriptionTrigger.MemberRemoved)] = SubscriptionStatus.Cancelled,
        [(SubscriptionStatus.PastDue, SubscriptionTrigger.MemberRemoved)] = SubscriptionStatus.Cancelled,
    };

    /// <summary>Every allowed transition, for tests and documentation.</summary>
    public static IEnumerable<(SubscriptionStatus From, SubscriptionTrigger Trigger, SubscriptionStatus To)> All =>
        Transitions.Select(static t => (t.Key.From, t.Key.Trigger, t.Value));

    /// <summary>The state <paramref name="trigger"/> leads to from <paramref name="from"/>, or null when it is not allowed.</summary>
    public static SubscriptionStatus? Next(SubscriptionStatus from, SubscriptionTrigger trigger) =>
        Transitions.TryGetValue((from, trigger), out var to) ? to : null;
}
