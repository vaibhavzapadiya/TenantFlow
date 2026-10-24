namespace TenantFlow.Payments.Contracts;

/// <summary>
/// Charges a subscription period through Payments (ADR-0023 decision 3, SPEC-0007 criteria 5 to 7), in the tenant of the
/// current tenant context. Asking again for the same subscription, period and attempt never charges again: it answers
/// with what became of the one payment of that attempt. The provider is called outside any transaction (AGENTS.md rule 3).
/// </summary>
public interface ISubscriptionCharges
{
    Task<SubscriptionChargeResult> ChargeAsync(SubscriptionCharge charge, CancellationToken cancellationToken);

    /// <summary>The payment of that subscription, period and attempt and its status, or null when none was started.</summary>
    Task<SubscriptionChargeResult?> FindAsync(Guid subscriptionId, int period, int attempt, CancellationToken cancellationToken);

    /// <summary>
    /// Gives back the succeeded charge of an attempt whose subscription ended before it settled (SPEC-0007 review fix R1):
    /// one refund per payment, of what it captured, with <paramref name="reason"/> as its audit reason. Asking again never
    /// refunds again; a charge that did not succeed is left alone.
    /// </summary>
    Task RefundAsync(Guid subscriptionId, int period, int attempt, string reason, CancellationToken cancellationToken);
}

/// <summary>What to charge: the subscription's price for one period, with its stored payment-method token.</summary>
/// <param name="SubscriptionId">The subscription being charged.</param>
/// <param name="Period">The billing period paid for, from 1.</param>
/// <param name="Attempt">Which try for that period, from 1; a failed attempt is followed by a new one.</param>
/// <param name="SubscriberMembershipId">The membership that subscribed.</param>
/// <param name="Amount">The price per period (AGENTS.md rule 4).</param>
/// <param name="Currency">ISO 4217 code of <paramref name="Amount"/>.</param>
/// <param name="PaymentMethod">The provider token stored with the subscription.</param>
public sealed record SubscriptionCharge(Guid SubscriptionId, int Period, int Attempt, Guid SubscriberMembershipId, decimal Amount, string Currency, string PaymentMethod);

/// <summary>Where a charge stands.</summary>
public enum SubscriptionChargeStatus
{
    /// <summary>Not settled yet (processing or unknown); ask again later.</summary>
    Pending = 1,

    Succeeded = 2,

    Failed = 3,

    /// <summary>Reconciliation gave up waiting; a late answer may still settle it (SPEC-0007 review fix R5).</summary>
    InReview = 4,
}

/// <summary>The one payment of a charge attempt and its status; <see cref="FailureCode"/> only when it failed.</summary>
public sealed record SubscriptionChargeResult(Guid PaymentId, SubscriptionChargeStatus Status, string? FailureCode);
