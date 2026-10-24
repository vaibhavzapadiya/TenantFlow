using TenantFlow.Payments.Application.Abstractions;
using TenantFlow.Payments.Application.Payments.Dtos;
using TenantFlow.Payments.Domain.Payments;

namespace TenantFlow.Payments.Application.Payments.Services;

/// <summary>
/// Sends a committed payment to the provider, outside any transaction, and commits the outcome (AGENTS.md rule 3), for
/// both order payments and subscription charges. A timeout or 5xx is unknown, never a failure, and is never retried here:
/// reconciliation settles it. The counterpart of <see cref="Refunds.Services.RefundSender"/>.
/// </summary>
internal static class PaymentSender
{
    /// <summary>Returns the committed payment and the provider's answer (null when the outcome is unknown).</summary>
    public static async Task<(PaymentDetails Settled, ProviderPaymentResult? Result)> SendAsync(
        IPaymentStore store, IPaymentProvider provider, Payment payment, CancellationToken cancellationToken)
    {
        ProviderPaymentResult? result;
        try
        {
            result = await provider.CreateAsync(
                new ProviderPaymentRequest(payment.Id, payment.TenantId, payment.Amount, payment.Currency, payment.PaymentMethod), cancellationToken);
        }
        catch (PaymentProviderUnavailableException)
        {
            result = null;
        }

        var settled = result is null
            ? await store.ApplyAsync(payment.Id, PaymentTrigger.OutcomeUnknown, null, null, cancellationToken)
            : await store.ApplyAsync(payment.Id, result.Outcome, result.ProviderPaymentId, result.FailureCode, cancellationToken);
        return (settled, result);
    }
}
