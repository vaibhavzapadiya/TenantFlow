namespace TenantFlow.Payments.Domain.Payments;

/// <summary>
/// The payment's state table (SPEC-0006 criteria 7 and 9, ADR-0022 decision 2): the one place that says which news moves a
/// payment where. Anything not listed is ignored, which is how a late or out-of-order answer is made harmless: a payment
/// that succeeded or failed never moves again.
/// </summary>
public static class PaymentStateMachine
{
    private static readonly Dictionary<(PaymentStatus From, PaymentTrigger Trigger), PaymentStatus> Transitions = new()
    {
        [(PaymentStatus.Processing, PaymentTrigger.Succeeded)] = PaymentStatus.Succeeded,
        [(PaymentStatus.Processing, PaymentTrigger.Failed)] = PaymentStatus.Failed,
        [(PaymentStatus.Processing, PaymentTrigger.Pending)] = PaymentStatus.Processing,
        [(PaymentStatus.Processing, PaymentTrigger.OutcomeUnknown)] = PaymentStatus.Unknown,
        [(PaymentStatus.Processing, PaymentTrigger.ReviewRequired)] = PaymentStatus.RequiresReview,
        [(PaymentStatus.Unknown, PaymentTrigger.Succeeded)] = PaymentStatus.Succeeded,
        [(PaymentStatus.Unknown, PaymentTrigger.Failed)] = PaymentStatus.Failed,
        [(PaymentStatus.Unknown, PaymentTrigger.ReviewRequired)] = PaymentStatus.RequiresReview,
        [(PaymentStatus.RequiresReview, PaymentTrigger.Succeeded)] = PaymentStatus.Succeeded,
        [(PaymentStatus.RequiresReview, PaymentTrigger.Failed)] = PaymentStatus.Failed,
    };

    /// <summary>Every allowed transition, for tests and documentation.</summary>
    public static IEnumerable<(PaymentStatus From, PaymentTrigger Trigger, PaymentStatus To)> All =>
        Transitions.Select(static t => (t.Key.From, t.Key.Trigger, t.Value));

    /// <summary>The state <paramref name="trigger"/> leads to from <paramref name="from"/>, or null when it is ignored.</summary>
    public static PaymentStatus? Next(PaymentStatus from, PaymentTrigger trigger) =>
        Transitions.TryGetValue((from, trigger), out var to) ? to : null;
}
