namespace TenantFlow.Payments.Application.Abstractions;

/// <summary>
/// The payment provider port (ADR-0022 decision 1). Adapters keep the provider's own request and response models to
/// themselves. Every call is remote: never call it inside a database transaction (AGENTS.md rule 3).
/// </summary>
public interface IPaymentProvider
{
    /// <summary>The adapter's stable name, stored on each payment (<c>simulator</c> or <c>stripe</c>).</summary>
    string Name { get; }

    /// <summary>
    /// Asks the provider to charge, with <see cref="ProviderPaymentRequest.PaymentId"/> as its idempotency key: sending the
    /// same request again never charges twice.
    /// </summary>
    /// <exception cref="PaymentProviderUnavailableException">No usable answer (timeout, 5xx, unreadable response): the outcome is unknown.</exception>
    Task<ProviderPaymentResult> CreateAsync(ProviderPaymentRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the provider what became of a payment (reconciliation, SPEC-0006 criterion 12): its final or pending outcome,
    /// or null when the provider has no record of it (the create call never reached it, or it is not visible yet).
    /// </summary>
    /// <exception cref="PaymentProviderUnavailableException">No usable answer; ask again later.</exception>
    Task<ProviderPaymentResult?> GetStatusAsync(ProviderStatusQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the provider to refund part or all of a payment it took (SPEC-0007 criterion 9), with
    /// <see cref="ProviderRefundRequest.RefundId"/> as its idempotency key: sending the same refund again never refunds twice.
    /// </summary>
    /// <exception cref="PaymentProviderUnavailableException">No usable answer: the outcome is unknown.</exception>
    Task<ProviderRefundResult> RefundAsync(ProviderRefundRequest request, CancellationToken cancellationToken);

    /// <summary>What became of a refund (reconciliation, SPEC-0007 criterion 12), or null when the provider has no record of it.</summary>
    /// <exception cref="PaymentProviderUnavailableException">No usable answer; ask again later.</exception>
    Task<ProviderRefundResult?> GetRefundStatusAsync(ProviderRefundQuery query, CancellationToken cancellationToken);
}
