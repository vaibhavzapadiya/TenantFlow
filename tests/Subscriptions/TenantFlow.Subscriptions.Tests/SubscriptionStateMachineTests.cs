using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.Subscriptions.Domain.Plans;
using TenantFlow.Subscriptions.Domain.Subscriptions;

namespace TenantFlow.Subscriptions.Tests;

/// <summary>SPEC-0007 criteria 2, 3 and 5 (ADR-0023 decision 2): the subscription's state table, its start and its cancel rules.</summary>
public sealed class SubscriptionStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 31, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_state_table_is_exactly_the_approved_one()
    {
        (SubscriptionStatus, SubscriptionTrigger, SubscriptionStatus)[] expected =
        [
            (SubscriptionStatus.Trialing, SubscriptionTrigger.CancelAtPeriodEnd, SubscriptionStatus.Trialing),
            (SubscriptionStatus.Active, SubscriptionTrigger.CancelAtPeriodEnd, SubscriptionStatus.Active),
            (SubscriptionStatus.Incomplete, SubscriptionTrigger.CancelNow, SubscriptionStatus.Cancelled),
            (SubscriptionStatus.PastDue, SubscriptionTrigger.CancelNow, SubscriptionStatus.Cancelled),
            (SubscriptionStatus.Trialing, SubscriptionTrigger.PeriodEnded, SubscriptionStatus.Cancelled),
            (SubscriptionStatus.Active, SubscriptionTrigger.PeriodEnded, SubscriptionStatus.Cancelled),
            (SubscriptionStatus.Trialing, SubscriptionTrigger.ChargeSucceeded, SubscriptionStatus.Active),
            (SubscriptionStatus.Incomplete, SubscriptionTrigger.ChargeSucceeded, SubscriptionStatus.Active),
            (SubscriptionStatus.Active, SubscriptionTrigger.ChargeSucceeded, SubscriptionStatus.Active),
            (SubscriptionStatus.PastDue, SubscriptionTrigger.ChargeSucceeded, SubscriptionStatus.Active),
            (SubscriptionStatus.Trialing, SubscriptionTrigger.ChargeFailed, SubscriptionStatus.PastDue),
            (SubscriptionStatus.Active, SubscriptionTrigger.ChargeFailed, SubscriptionStatus.PastDue),
            (SubscriptionStatus.PastDue, SubscriptionTrigger.ChargeFailed, SubscriptionStatus.PastDue),
            (SubscriptionStatus.Incomplete, SubscriptionTrigger.ChargeFailed, SubscriptionStatus.Cancelled),
            (SubscriptionStatus.PastDue, SubscriptionTrigger.AttemptsExhausted, SubscriptionStatus.Cancelled),
            (SubscriptionStatus.Trialing, SubscriptionTrigger.ChargeInReview, SubscriptionStatus.PastDue),
            (SubscriptionStatus.Active, SubscriptionTrigger.ChargeInReview, SubscriptionStatus.PastDue),
            (SubscriptionStatus.PastDue, SubscriptionTrigger.ChargeInReview, SubscriptionStatus.PastDue),
            (SubscriptionStatus.Incomplete, SubscriptionTrigger.ChargeInReview, SubscriptionStatus.Incomplete),
            (SubscriptionStatus.Trialing, SubscriptionTrigger.MemberRemoved, SubscriptionStatus.Cancelled),
            (SubscriptionStatus.Incomplete, SubscriptionTrigger.MemberRemoved, SubscriptionStatus.Cancelled),
            (SubscriptionStatus.Active, SubscriptionTrigger.MemberRemoved, SubscriptionStatus.Cancelled),
            (SubscriptionStatus.PastDue, SubscriptionTrigger.MemberRemoved, SubscriptionStatus.Cancelled),
        ];

        Assert.Equal(expected.Order(), SubscriptionStateMachine.All.Order());
        Assert.DoesNotContain(SubscriptionStateMachine.All, static t => t.From == SubscriptionStatus.Cancelled);
    }

    [Fact]
    public void A_trial_starts_trialing_until_its_end_and_no_trial_is_due_at_once()
    {
        var trial = Subscription.Start(Guid.NewGuid(), NewPlan(trialDays: 14), Guid.NewGuid(), "sim_success", Now);
        var direct = Subscription.Start(Guid.NewGuid(), NewPlan(trialDays: 0), Guid.NewGuid(), "sim_success", Now);

        Assert.Equal((SubscriptionStatus.Trialing, Now.AddDays(14), 0), (trial.Status, trial.PeriodEnd, trial.Period));
        Assert.Equal((SubscriptionStatus.Incomplete, Now, 0), (direct.Status, direct.PeriodEnd, direct.Period));
        Assert.Equal((9.99m, "EUR"), (direct.Amount, direct.Currency));
    }

    [Fact]
    public void A_card_number_is_never_accepted_as_a_payment_method()
    {
        var error = Assert.Throws<AppException>(() => Subscription.Start(Guid.NewGuid(), NewPlan(0), Guid.NewGuid(), "pm_4242_4242_4242_4242", Now));

        Assert.Equal(SubscriptionErrors.PaymentMethodInvalid, error.Error);
    }

    [Fact]
    public void Cancelling_waits_for_the_period_end_when_one_runs_and_ends_at_once_otherwise()
    {
        var trial = Subscription.Start(Guid.NewGuid(), NewPlan(14), Guid.NewGuid(), "sim_success", Now);
        var incomplete = Subscription.Start(Guid.NewGuid(), NewPlan(0), Guid.NewGuid(), "sim_success", Now);

        trial.Cancel(Now);
        incomplete.Cancel(Now);

        Assert.Equal((SubscriptionStatus.Trialing, true), (trial.Status, trial.CancelAtPeriodEnd));
        Assert.Equal((SubscriptionStatus.Cancelled, Subscription.CancelledOnRequest), (incomplete.Status, incomplete.CancelReason));
        Assert.Equal(SubscriptionErrors.InvalidTransition, Assert.Throws<AppException>(() => incomplete.Cancel(Now)).Error);
    }

    [Fact]
    public void Monthly_periods_count_from_the_anchor_so_a_month_end_does_not_drift()
    {
        var anchor = new DateTimeOffset(2026, 1, 31, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            [new DateTimeOffset(2026, 2, 28, 9, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 31, 9, 0, 0, TimeSpan.Zero)],
            new[] { BillingInterval.Month.PeriodEnd(anchor, 1), BillingInterval.Month.PeriodEnd(anchor, 2) });
        Assert.Equal(new DateTimeOffset(2027, 1, 31, 9, 0, 0, TimeSpan.Zero), BillingInterval.Year.PeriodEnd(anchor, 1));
    }

    [Fact]
    public void A_paid_charge_advances_one_period_and_failed_charges_end_after_the_last_attempt()
    {
        var paid = Subscription.Start(Guid.NewGuid(), NewPlan(0), Guid.NewGuid(), "sim_success", Now);
        var neverStarted = Subscription.Start(Guid.NewGuid(), NewPlan(0), Guid.NewGuid(), "sim_success", Now);
        var lapsing = Subscription.Start(Guid.NewGuid(), NewPlan(14), Guid.NewGuid(), "sim_success", Now);

        paid.ChargeSucceeded(Now);
        neverStarted.ChargeFailed(3, Now);
        var lapses = new List<SubscriptionStatus>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            lapsing.ChargeFailed(3, Now);
            lapses.Add(lapsing.Status);
        }

        Assert.Equal((SubscriptionStatus.Active, 1, Now.AddMonths(1), 0), (paid.Status, paid.Period, paid.PeriodEnd, paid.FailedAttempts));
        Assert.Equal((SubscriptionStatus.Cancelled, Subscription.CancelledForNonPayment), (neverStarted.Status, neverStarted.CancelReason));
        Assert.Equal([SubscriptionStatus.PastDue, SubscriptionStatus.PastDue, SubscriptionStatus.Cancelled], lapses);
        Assert.Equal(Subscription.CancelledForNonPayment, lapsing.CancelReason);
    }

    private static Plan NewPlan(int trialDays) => Plan.Create(Guid.NewGuid(), Guid.NewGuid(), "Pro", 9.99m, "eur", "month", trialDays, Now);
}
