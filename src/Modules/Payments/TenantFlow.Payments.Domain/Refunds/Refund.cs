using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Abstractions.Text;
using TenantFlow.Payments.Domain.Payments;

namespace TenantFlow.Payments.Domain.Refunds;

/// <summary>
/// Money given back from an order's payment (SPEC-0007 criteria 9 to 14, ADR-0023 decision 4): a workflow of its own,
/// with its own idempotency key, state and audit fields (who asked, why, when). Its amount is in the payment's currency
/// (AGENTS.md rule 4); its id is the idempotency key the provider sees, so however often it is sent it refunds once. It
/// moves by the same state table as a payment (<see cref="PaymentStateMachine"/>): processing, then succeeded or failed,
/// or unknown and reconciled.
/// </summary>
public sealed class Refund
{
    public const int MaxReasonLength = 500;

    /// <summary>A refund an <c>orders.manage</c> holder asked for.</summary>
    public const string RefundKind = "refund";

    /// <summary>The refund of what is left when a paid order is cancelled; at most one per payment.</summary>
    public const string CancellationKind = "cancellation";

    private Refund()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid PaymentId { get; private set; }

    /// <summary>The refunded order, or null for the refund of a subscription charge (review fix R1).</summary>
    public Guid? OrderId { get; private set; }

    public decimal Amount { get; private set; }

    /// <summary>ISO 4217 code of <see cref="Amount"/>: the payment's.</summary>
    public string Currency { get; private set; } = string.Empty;

    public PaymentStatus Status { get; private set; }

    /// <summary><see cref="RefundKind"/> or <see cref="CancellationKind"/>.</summary>
    public string Kind { get; private set; } = string.Empty;

    /// <summary>Why, as the requester wrote it (audit).</summary>
    public string Reason { get; private set; } = string.Empty;

    /// <summary>Who asked (audit): an <c>orders.manage</c> holder's membership.</summary>
    public Guid RequestedByMembershipId { get; private set; }

    /// <summary>The adapter that took the payment, and so takes its refunds.</summary>
    public string Provider { get; private set; } = string.Empty;

    /// <summary>The provider's own id for the refund, once it has answered.</summary>
    public string? ProviderRefundId { get; private set; }

    public string? FailureCode { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static AppError? CheckAmount(decimal? amount) =>
        amount is { } value && value > 0 && value.Scale <= 4 ? null : PaymentErrors.RefundAmountInvalid;

    public static AppError? CheckReason(string? reason) =>
        reason?.Trim() is { Length: > 0 and <= MaxReasonLength } trimmed && !TextRules.ContainsDisallowedCharacters(trimmed) ? null : PaymentErrors.RefundReasonInvalid;

    /// <summary>
    /// Starts a refund as <see cref="PaymentStatus.Processing"/>: committed before the provider is called. A refund an
    /// <c>orders.manage</c> holder asks for is of an order's payment; a cancellation refund may also give back a
    /// subscription charge.
    /// </summary>
    /// <exception cref="AppException">An input breaks a rule; see the <c>Check</c> methods.</exception>
    /// <exception cref="ArgumentException">An id is empty, or the payment is not an order's: a programming error.</exception>
    public static Refund Start(Guid id, Payment payment, decimal? amount, string kind, string? reason, Guid requestedByMembershipId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(payment);
        if (id == Guid.Empty || requestedByMembershipId == Guid.Empty || kind is not (RefundKind or CancellationKind) || (kind == RefundKind && payment.OrderId is null))
        {
            throw new ArgumentException("A refund needs an id, a requester and a kind; a requested refund needs an order's payment.");
        }

        if ((CheckAmount(amount) ?? CheckReason(reason)) is { } error)
        {
            throw new AppException(error);
        }

        return new Refund
        {
            Id = id,
            TenantId = payment.TenantId,
            PaymentId = payment.Id,
            OrderId = payment.OrderId,
            Amount = amount!.Value,
            Currency = payment.Currency,
            Status = PaymentStatus.Processing,
            Kind = kind,
            Reason = reason!.Trim(),
            RequestedByMembershipId = requestedByMembershipId,
            Provider = payment.Provider,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Applies news about the refund by the state table. True when the status changed. The provider's id is kept once known.</summary>
    public bool Apply(PaymentTrigger trigger, string? providerRefundId, string? failureCode, DateTimeOffset now)
    {
        if (PaymentStateMachine.Next(Status, trigger) is not { } next)
        {
            return false;
        }

        ProviderRefundId ??= providerRefundId;
        var changed = next != Status;
        Status = next;
        FailureCode = next == PaymentStatus.Failed ? failureCode : FailureCode;
        UpdatedAt = now;
        return changed;
    }
}
