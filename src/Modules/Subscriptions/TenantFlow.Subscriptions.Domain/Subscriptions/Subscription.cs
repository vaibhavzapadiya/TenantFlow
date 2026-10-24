using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.Payments.Contracts;
using TenantFlow.Subscriptions.Domain.Plans;

namespace TenantFlow.Subscriptions.Domain.Subscriptions;

/// <summary>
/// A member's subscription to a plan (SPEC-0007, ADR-0023 decision 2). It copies the plan's price and interval when it
/// starts, holds a provider token for later charges (never card data), and counts paid periods from its anchor:
/// <see cref="PeriodEnd"/> is when the charge for period <see cref="Period"/> + 1 is due. How it moves is
/// <see cref="SubscriptionStateMachine"/>'s answer.
/// </summary>
public sealed class Subscription
{
    public const string CancelledOnRequest = "requested";
    public const string CancelledForNonPayment = "payment_failed";
    public const string CancelledForRemovedMember = "member_removed";

    private Subscription()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid PlanId { get; private set; }

    /// <summary>The membership that subscribed: the only member besides <c>subscriptions.manage</c> holders who may see it.</summary>
    public Guid SubscriberMembershipId { get; private set; }

    public SubscriptionStatus Status { get; private set; }

    /// <summary>The provider token charged each period (for example <c>pm_card_visa</c>); never a card number.</summary>
    public string PaymentMethod { get; private set; } = string.Empty;

    /// <summary>The price per period, copied from the plan when the subscription started.</summary>
    public decimal Amount { get; private set; }

    /// <summary>ISO 4217 code of <see cref="Amount"/>.</summary>
    public string Currency { get; private set; } = string.Empty;

    public BillingInterval Interval { get; private set; }

    /// <summary>When period 1 starts: the end of the trial, or the moment of subscribing without one.</summary>
    public DateTimeOffset AnchorAt { get; private set; }

    /// <summary>How many periods are paid.</summary>
    public int Period { get; private set; }

    /// <summary>The end of the paid (or trial) time: the next charge is due then.</summary>
    public DateTimeOffset PeriodEnd { get; private set; }

    /// <summary>How many charges for the next period have failed so far.</summary>
    public int FailedAttempts { get; private set; }

    /// <summary>True once cancelled to take effect at <see cref="PeriodEnd"/>; no charge is made after it.</summary>
    public bool CancelAtPeriodEnd { get; private set; }

    /// <summary><see cref="CancelledOnRequest"/>, <see cref="CancelledForNonPayment"/> or <see cref="CancelledForRemovedMember"/>; null unless cancelled.</summary>
    public string? CancelReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static AppError? CheckPaymentMethod(string? paymentMethod) =>
        PaymentMethodTokens.IsValid(paymentMethod) ? null : SubscriptionErrors.PaymentMethodInvalid;

    /// <summary>
    /// Starts a subscription to <paramref name="plan"/>: <see cref="SubscriptionStatus.Trialing"/> until the trial ends, or
    /// <see cref="SubscriptionStatus.Incomplete"/> with its first charge due at once (SPEC-0007 criterion 2).
    /// </summary>
    /// <exception cref="AppException"><see cref="SubscriptionErrors.PaymentMethodInvalid"/>.</exception>
    /// <exception cref="ArgumentException">An id is empty or the plan belongs to another tenant: a programming error.</exception>
    public static Subscription Start(Guid id, Plan plan, Guid subscriberMembershipId, string? paymentMethod, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (id == Guid.Empty || subscriberMembershipId == Guid.Empty)
        {
            throw new ArgumentException("A subscription needs an id and a subscriber.");
        }

        if (CheckPaymentMethod(paymentMethod) is { } error)
        {
            throw new AppException(error);
        }

        var anchor = now.AddDays(plan.TrialDays);
        return new Subscription
        {
            Id = id,
            TenantId = plan.TenantId,
            PlanId = plan.Id,
            SubscriberMembershipId = subscriberMembershipId,
            Status = plan.TrialDays > 0 ? SubscriptionStatus.Trialing : SubscriptionStatus.Incomplete,
            PaymentMethod = paymentMethod!,
            Amount = plan.Amount,
            Currency = plan.Currency,
            Interval = plan.Interval,
            AnchorAt = anchor,
            Period = 0,
            PeriodEnd = anchor,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Cancels (SPEC-0007 criterion 3): with a paid or trial period still running, at its end; otherwise at once. Asking
    /// again while a cancellation is pending changes nothing.
    /// </summary>
    /// <exception cref="AppException"><see cref="SubscriptionErrors.InvalidTransition"/> when it is already cancelled.</exception>
    public void Cancel(DateTimeOffset now)
    {
        if (SubscriptionStateMachine.Next(Status, SubscriptionTrigger.CancelNow) is { } ended)
        {
            Status = ended;
            CancelReason = CancelledOnRequest;
        }
        else if (SubscriptionStateMachine.Next(Status, SubscriptionTrigger.CancelAtPeriodEnd) is not null)
        {
            CancelAtPeriodEnd = true;
        }
        else
        {
            throw new AppException(SubscriptionErrors.InvalidTransition);
        }

        UpdatedAt = now;
    }

    /// <summary>
    /// Ends a subscription cancelled at the end of its period once that period is over (SPEC-0007 criterion 3). False, with
    /// nothing changed, when it was not cancelled or its period still runs.
    /// </summary>
    public bool EndIfCancelled(DateTimeOffset now)
    {
        if (!CancelAtPeriodEnd || now < PeriodEnd || SubscriptionStateMachine.Next(Status, SubscriptionTrigger.PeriodEnded) is not { } ended)
        {
            return false;
        }

        Status = ended;
        CancelReason = CancelledOnRequest;
        UpdatedAt = now;
        return true;
    }

    /// <summary>The next period is paid (SPEC-0007 criterion 5): it starts where the last ended, counted from the anchor.</summary>
    /// <exception cref="AppException"><see cref="SubscriptionErrors.InvalidTransition"/> when it is cancelled.</exception>
    public void ChargeSucceeded(DateTimeOffset now)
    {
        Status = SubscriptionStateMachine.Next(Status, SubscriptionTrigger.ChargeSucceeded) ?? throw new AppException(SubscriptionErrors.InvalidTransition);
        Period++;
        PeriodEnd = Interval.PeriodEnd(AnchorAt, Period);
        FailedAttempts = 0;
        UpdatedAt = now;
    }

    /// <summary>
    /// A charge for the next period failed (SPEC-0007 criterion 7): a first charge ends a subscription that never started;
    /// otherwise it is past due, and once <paramref name="maxAttempts"/> attempts failed it is cancelled.
    /// </summary>
    /// <exception cref="AppException"><see cref="SubscriptionErrors.InvalidTransition"/> when it is cancelled.</exception>
    public void ChargeFailed(int maxAttempts, DateTimeOffset now)
    {
        FailedAttempts++;
        var trigger = Status == SubscriptionStatus.PastDue && FailedAttempts >= maxAttempts ? SubscriptionTrigger.AttemptsExhausted : SubscriptionTrigger.ChargeFailed;
        Status = SubscriptionStateMachine.Next(Status, trigger) ?? throw new AppException(SubscriptionErrors.InvalidTransition);
        CancelReason = Status == SubscriptionStatus.Cancelled ? CancelledForNonPayment : CancelReason;
        UpdatedAt = now;
    }

    /// <summary>The charge for the next period is in review (review fix R5): the period it covers is not granted meanwhile.</summary>
    /// <returns>True when the status changed.</returns>
    public bool ChargeInReview(DateTimeOffset now)
    {
        var next = SubscriptionStateMachine.Next(Status, SubscriptionTrigger.ChargeInReview) ?? throw new AppException(SubscriptionErrors.InvalidTransition);
        var changed = next != Status;
        Status = next;
        UpdatedAt = changed ? now : UpdatedAt;
        return changed;
    }

    /// <summary>Ends the subscription of a member who was removed from the tenant (review fix R9): nothing is charged again.</summary>
    public void EndForRemovedMember(DateTimeOffset now)
    {
        Status = SubscriptionStateMachine.Next(Status, SubscriptionTrigger.MemberRemoved) ?? throw new AppException(SubscriptionErrors.InvalidTransition);
        CancelReason = CancelledForRemovedMember;
        UpdatedAt = now;
    }

    /// <summary>
    /// After an outage, skips the periods that ended entirely while nothing ran (review fix R12): an active subscription is
    /// charged for the period that contains <paramref name="now"/>, not for each period missed, in a burst. The skipped
    /// periods are not charged. True when a period was skipped.
    /// </summary>
    public bool SkipMissedPeriods(DateTimeOffset now)
    {
        if (Status != SubscriptionStatus.Active || CancelAtPeriodEnd)
        {
            return false;
        }

        var skipped = false;
        while (Interval.PeriodEnd(AnchorAt, Period + 1) <= now)
        {
            Period++;
            skipped = true;
        }

        if (skipped)
        {
            PeriodEnd = Interval.PeriodEnd(AnchorAt, Period);
            UpdatedAt = now;
        }

        return skipped;
    }

    public bool IsOwnedBy(Guid membershipId) => SubscriberMembershipId == membershipId;
}
