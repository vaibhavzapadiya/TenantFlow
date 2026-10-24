using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.Payments.Contracts;

namespace TenantFlow.Payments.Domain.Payments;

/// <summary>
/// One charge (SPEC-0006, ADR-0022 decision 2): the payment of an order, for the order's total in the order's currency, or
/// one attempt to charge a subscription period (SPEC-0007, ADR-0023 decision 3); exactly one of the two subjects is set.
/// Amounts are decimal plus currency (AGENTS.md rule 4). Its id is the idempotency key the provider sees, so however often
/// it is sent it charges once. It holds a provider token for the payment method, never card data. How it moves is
/// <see cref="PaymentStateMachine"/>'s answer.
/// </summary>
public sealed class Payment
{
    public const int MaxPaymentMethodLength = PaymentMethodTokens.MaxLength;
    public const int MaxIdempotencyKeyLength = 255;

    private Payment()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>The order paid, or null for a subscription charge.</summary>
    public Guid? OrderId { get; private set; }

    /// <summary>The subscription charged, or null for an order payment.</summary>
    public Guid? SubscriptionId { get; private set; }

    /// <summary>The billing period a subscription charge pays for, from 1.</summary>
    public int? BillingPeriod { get; private set; }

    /// <summary>Which attempt for that period a subscription charge is, from 1.</summary>
    public int? ChargeAttempt { get; private set; }

    /// <summary>The membership that began the payment: the order's owner, who may read it (with <c>orders.manage</c> holders), or the subscriber.</summary>
    public Guid OwnerMembershipId { get; private set; }

    public decimal Amount { get; private set; }

    /// <summary>ISO 4217 code of <see cref="Amount"/>.</summary>
    public string Currency { get; private set; } = string.Empty;

    public PaymentStatus Status { get; private set; }

    /// <summary>The adapter that handles it (<c>simulator</c> or <c>stripe</c>).</summary>
    public string Provider { get; private set; } = string.Empty;

    /// <summary>The provider's own id for it, once the provider has answered.</summary>
    public string? ProviderPaymentId { get; private set; }

    /// <summary>The provider token the customer chose (for example <c>pm_card_visa</c>); never a card number.</summary>
    public string PaymentMethod { get; private set; } = string.Empty;

    /// <summary>The provider's reason for a failure, as a short code; null unless <see cref="PaymentStatus.Failed"/>.</summary>
    public string? FailureCode { get; private set; }

    /// <summary>What succeeded refunds gave back so far; never more than <see cref="Amount"/> (SPEC-0007 criterion 10).</summary>
    public decimal RefundedAmount { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Visible ASCII only, as RFC 9110 allows in a header value without quoting.</summary>
    public static AppError? CheckIdempotencyKey(string? key) =>
        key is { Length: > 0 and <= MaxIdempotencyKeyLength } && key.All(static c => c is > ' ' and <= '~')
            ? null
            : PaymentErrors.IdempotencyKeyInvalid;

    /// <summary>A provider token (<see cref="PaymentMethodTokens"/>). A rejected value is never echoed, stored or logged.</summary>
    public static AppError? CheckPaymentMethod(string? paymentMethod) =>
        PaymentMethodTokens.IsValid(paymentMethod) ? null : PaymentErrors.PaymentMethodInvalid;

    /// <summary>Starts a payment as <see cref="PaymentStatus.Processing"/>: committed before the provider is called.</summary>
    /// <exception cref="ArgumentException">An id is empty or the amount is not positive: a programming error.</exception>
    public static Payment Start(Guid id, Guid tenantId, Guid orderId, Guid ownerMembershipId, decimal amount, string currency, string provider, string paymentMethod, DateTimeOffset now)
    {
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("An order payment needs an order.", nameof(orderId));
        }

        var payment = New(id, tenantId, ownerMembershipId, amount, currency, provider, paymentMethod, now);
        payment.OrderId = orderId;
        return payment;
    }

    /// <summary>
    /// Starts one attempt to charge a subscription period (SPEC-0007 criterion 5), as <see cref="PaymentStatus.Processing"/>:
    /// committed before the provider is called.
    /// </summary>
    /// <exception cref="ArgumentException">An id is empty, the amount is not positive, or the period or attempt is below 1: a programming error.</exception>
    public static Payment StartCharge(Guid id, Guid tenantId, Guid subscriptionId, int billingPeriod, int chargeAttempt, Guid subscriberMembershipId, decimal amount, string currency, string provider, string paymentMethod, DateTimeOffset now)
    {
        if (subscriptionId == Guid.Empty || billingPeriod < 1 || chargeAttempt < 1)
        {
            throw new ArgumentException("A subscription charge needs a subscription, a period and an attempt.");
        }

        var payment = New(id, tenantId, subscriberMembershipId, amount, currency, provider, paymentMethod, now);
        payment.SubscriptionId = subscriptionId;
        payment.BillingPeriod = billingPeriod;
        payment.ChargeAttempt = chargeAttempt;
        return payment;
    }

    /// <summary>
    /// Applies news about the payment by the state table. True when the status changed; news the table does not allow
    /// changes nothing (a late or duplicate answer). The provider's id is kept once known.
    /// </summary>
    public bool Apply(PaymentTrigger trigger, string? providerPaymentId, string? failureCode, DateTimeOffset now)
    {
        if (PaymentStateMachine.Next(Status, trigger) is not { } next)
        {
            return false;
        }

        ProviderPaymentId ??= providerPaymentId;
        var changed = next != Status;
        Status = next;
        FailureCode = next == PaymentStatus.Failed ? failureCode : FailureCode;
        UpdatedAt = now;
        return changed;
    }

    public bool IsOwnedBy(Guid membershipId) => OwnerMembershipId == membershipId;

    /// <summary>Counts a succeeded refund. The store has checked it fits the captured amount, under the payment's lock.</summary>
    /// <exception cref="InvalidOperationException">It would exceed the captured amount: a programming error.</exception>
    public void RecordRefund(decimal amount, DateTimeOffset now)
    {
        if (RefundedAmount + amount > Amount)
        {
            throw new InvalidOperationException("Refunds cannot exceed the captured amount.");
        }

        RefundedAmount += amount;
        UpdatedAt = now;
    }

    private static Payment New(Guid id, Guid tenantId, Guid ownerMembershipId, decimal amount, string currency, string provider, string paymentMethod, DateTimeOffset now)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || ownerMembershipId == Guid.Empty || amount <= 0)
        {
            throw new ArgumentException("A payment needs an id, a tenant, an owner and a positive amount.");
        }

        if (CheckPaymentMethod(paymentMethod) is { } error)
        {
            throw new AppException(error);
        }

        return new Payment
        {
            Id = id,
            TenantId = tenantId,
            OwnerMembershipId = ownerMembershipId,
            Amount = amount,
            Currency = currency,
            Status = PaymentStatus.Processing,
            Provider = provider,
            PaymentMethod = paymentMethod,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
