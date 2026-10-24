using TenantFlow.Payments.Application.Abstractions;
using TenantFlow.Payments.Application.Refunds.Dtos;
using TenantFlow.Payments.Domain.Payments;
using TenantFlow.Payments.Domain.Refunds;

namespace TenantFlow.Payments.Application.Refunds.Services;

/// <summary>
/// Sends a committed refund to the provider that took its payment (never the one configured for new payments), outside any
/// transaction, and commits the outcome (AGENTS.md rule 3). A timeout or 5xx is unknown, never a failure, and is never
/// retried here: reconciliation settles it.
/// </summary>
public sealed class RefundSender(IPaymentStore store, PaymentProviders providers)
{
    public async Task<RefundDetails> SendAsync(Refund refund, Payment payment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(refund);
        ArgumentNullException.ThrowIfNull(payment);
        var provider = providers.Find(refund.Provider)
            ?? throw new InvalidOperationException("The provider that took this payment is not registered in this host.");
        ProviderRefundResult? result;
        try
        {
            result = await provider.RefundAsync(
                new ProviderRefundRequest(refund.Id, refund.TenantId, payment.Id, payment.ProviderPaymentId, payment.PaymentMethod, refund.Amount, refund.Currency),
                cancellationToken);
        }
        catch (PaymentProviderUnavailableException)
        {
            result = null;
        }

        return result is null
            ? await store.ApplyRefundAsync(refund.Id, PaymentTrigger.OutcomeUnknown, null, null, cancellationToken)
            : await store.ApplyRefundAsync(refund.Id, result.Outcome, result.ProviderRefundId, result.FailureCode, cancellationToken);
    }
}
