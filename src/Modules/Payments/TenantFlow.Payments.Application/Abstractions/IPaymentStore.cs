using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.Payments.Application.Payments.Dtos;
using TenantFlow.Payments.Application.Refunds.Dtos;
using TenantFlow.Payments.Domain.Payments;
using TenantFlow.Payments.Domain.Refunds;

namespace TenantFlow.Payments.Application.Abstractions;

/// <summary>
/// Storage for the Payments use cases. Every method acts on the tenant in the current tenant context: it takes no tenant
/// id, filters by it, and runs in a transaction that sets it for row-level security (ADR-0011). With no tenant set it
/// throws before touching data.
/// </summary>
public interface IPaymentStore
{
    /// <summary>The request stored under an idempotency key of this tenant, or null when the key is new.</summary>
    Task<IdempotentRequest?> FindRequestAsync(string operation, string key, CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction: claims the idempotency key (unfinished, with the request's hash), inserts the payment, and
    /// schedules its reconciliation (ADR-0022 decision 7). Nothing is written when the key is already taken or the order
    /// already has a payment.
    /// </summary>
    Task<StartResult> TryStartAsync(Payment payment, IdempotencyKeyRef key, string requestHash, CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction: applies <paramref name="trigger"/> by the state table, writes the matching event to the outbox
    /// when the status changed (ADR-0021), and stores the resulting payment as the response of every unfinished
    /// idempotency key of this payment, so a request interrupted after its key was claimed is answered, not refused,
    /// on its next repeat. Returns the payment as committed.
    /// </summary>
    Task<PaymentDetails> ApplyAsync(Guid paymentId, PaymentTrigger trigger, string? providerPaymentId, string? failureCode, CancellationToken cancellationToken);

    /// <summary>The payment, or null.</summary>
    Task<Payment?> FindAsync(Guid paymentId, CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction of <paramref name="tenantId"/> (taken from a verified webhook, never from a user): records the
    /// event id, so a duplicate changes nothing; requires the payment to exist in that tenant with the same provider and
    /// provider reference; applies the outcome by the state table, with its event when the status changed (ADR-0021).
    /// Nothing is written for <see cref="WebhookOutcome.Duplicate"/> or <see cref="WebhookOutcome.NotFound"/>.
    /// </summary>
    Task<WebhookOutcome> ApplyWebhookAsync(Guid tenantId, string provider, ProviderWebhookEvent webhook, CancellationToken cancellationToken);

    /// <summary>The order's payment, or null.</summary>
    Task<Payment?> FindByOrderAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction: inserts a subscription charge and schedules its reconciliation (ADR-0022 decision 7). False, with
    /// nothing written, when that subscription, period and attempt already has its payment (ADR-0023 decision 3).
    /// </summary>
    Task<bool> TryStartChargeAsync(Payment payment, CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction, with the refunded payment locked (SPEC-0007 criterion 10): claims the idempotency key when one is
    /// given; requires the payment to have succeeded; requires the refunds that have not failed plus this one to fit the
    /// captured amount (for a cancellation refund, waits until every other refund is settled, sets its amount to what is
    /// left, and allows one that has not failed per payment); inserts the
    /// refund, schedules its reconciliation and writes <paramref name="audit"/> to the outbox when given. Nothing is written
    /// unless the answer is <see cref="RefundStart.Started"/>.
    /// </summary>
    Task<RefundStart> TryStartRefundAsync(Refund refund, IdempotencyKeyRef? key, string? requestHash, OutgoingMessage? audit, CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction, with the payment locked: applies <paramref name="trigger"/> to the refund by the state table; on
    /// success counts it in the payment's refunded amount; writes the matching event when the status changed; and answers
    /// the refund's unfinished idempotency keys. Returns the refund as committed.
    /// </summary>
    Task<RefundDetails> ApplyRefundAsync(Guid refundId, PaymentTrigger trigger, string? providerRefundId, string? failureCode, CancellationToken cancellationToken);

    /// <summary>The refund, or null.</summary>
    Task<Refund?> FindRefundAsync(Guid refundId, CancellationToken cancellationToken);

    /// <summary>The payment's cancellation refund that has not failed, or null (a failed one may be followed by another).</summary>
    Task<Refund?> FindCancellationRefundAsync(Guid paymentId, CancellationToken cancellationToken);

    /// <summary>The payment's refunds, oldest first.</summary>
    Task<IReadOnlyList<Refund>> ListRefundsAsync(Guid paymentId, CancellationToken cancellationToken);

    /// <summary>The payment of a subscription's period and attempt, or null.</summary>
    Task<Payment?> FindChargeAsync(Guid subscriptionId, int billingPeriod, int chargeAttempt, CancellationToken cancellationToken);
}
