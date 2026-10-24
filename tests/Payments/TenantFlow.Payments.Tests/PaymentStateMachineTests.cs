using TenantFlow.Payments.Domain.Payments;

namespace TenantFlow.Payments.Tests;

/// <summary>SPEC-0006 criteria 7 and 9 (ADR-0022 decision 2): the payment's state table is exactly this, and a final payment never moves.</summary>
public sealed class PaymentStateMachineTests
{
    [Fact]
    public void The_state_table_is_exactly_the_approved_one()
    {
        (PaymentStatus, PaymentTrigger, PaymentStatus)[] expected =
        [
            (PaymentStatus.Processing, PaymentTrigger.Succeeded, PaymentStatus.Succeeded),
            (PaymentStatus.Processing, PaymentTrigger.Failed, PaymentStatus.Failed),
            (PaymentStatus.Processing, PaymentTrigger.Pending, PaymentStatus.Processing),
            (PaymentStatus.Processing, PaymentTrigger.OutcomeUnknown, PaymentStatus.Unknown),
            (PaymentStatus.Processing, PaymentTrigger.ReviewRequired, PaymentStatus.RequiresReview),
            (PaymentStatus.Unknown, PaymentTrigger.Succeeded, PaymentStatus.Succeeded),
            (PaymentStatus.Unknown, PaymentTrigger.Failed, PaymentStatus.Failed),
            (PaymentStatus.Unknown, PaymentTrigger.ReviewRequired, PaymentStatus.RequiresReview),
            (PaymentStatus.RequiresReview, PaymentTrigger.Succeeded, PaymentStatus.Succeeded),
            (PaymentStatus.RequiresReview, PaymentTrigger.Failed, PaymentStatus.Failed),
        ];

        Assert.Equal(expected.Order(), PaymentStateMachine.All.Order());
    }

    [Fact]
    public void A_settled_payment_ignores_every_later_answer_and_keeps_its_failure_code()
    {
        var now = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
        var succeeded = NewPayment(now);
        var failed = NewPayment(now);
        succeeded.Apply(PaymentTrigger.Succeeded, "pi_1", null, now);
        failed.Apply(PaymentTrigger.Failed, "pi_2", "card_declined", now);

        var changed = Enum.GetValues<PaymentTrigger>()
            .Select(trigger => succeeded.Apply(trigger, "pi_other", "late", now) || failed.Apply(trigger, "pi_other", "late", now))
            .ToList();

        Assert.DoesNotContain(true, changed);
        Assert.Equal((PaymentStatus.Succeeded, "pi_1", null), (succeeded.Status, succeeded.ProviderPaymentId, succeeded.FailureCode));
        Assert.Equal((PaymentStatus.Failed, "pi_2", "card_declined"), (failed.Status, failed.ProviderPaymentId, failed.FailureCode));
    }

    private static Payment NewPayment(DateTimeOffset now) =>
        Payment.Start(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 10m, "EUR", "simulator", "sim_success", now);
}
