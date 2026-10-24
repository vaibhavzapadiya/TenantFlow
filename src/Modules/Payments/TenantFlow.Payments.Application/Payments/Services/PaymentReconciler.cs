using TenantFlow.BuildingBlocks.Abstractions.Workflow;
using TenantFlow.Payments.Application.Abstractions;
using TenantFlow.Payments.Domain.Payments;

namespace TenantFlow.Payments.Application.Payments.Services;

/// <summary>
/// Settles payments whose outcome is not known yet (SPEC-0006 criterion 12): asks the provider, outside any transaction,
/// and applies a final answer through the state table, exactly as a webhook would. It never charges: a payment the
/// provider has no record of is counted as unknown, never re-sent. After <see cref="PaymentReconciliation.MaxChecks"/>
/// checks without a final answer the payment needs review (and announces it); checks then continue more slowly. Runs in
/// the payment's tenant (the Worker enters it from the timer), against the provider that took the payment; an unknown
/// provider is an error the timer retries, never an answer.
/// </summary>
public sealed class PaymentReconciler(IPaymentStore store, PaymentProviders providers, TimeProvider clock)
{
    /// <param name="paymentId">The timer's subject.</param>
    /// <param name="checksSoFar">How often this payment was checked before (the timer's attempts).</param>
    /// <param name="cancellationToken">Stops the check; the timer's lease brings it back.</param>
    public async Task<TimerDecision> ReconcileAsync(Guid paymentId, int checksSoFar, CancellationToken cancellationToken)
    {
        var payment = await store.FindAsync(paymentId, cancellationToken);
        if (payment is null || payment.Status is PaymentStatus.Succeeded or PaymentStatus.Failed)
        {
            return TimerDecision.Complete;
        }

        // The provider that took the payment, never the one configured for new payments now.
        var provider = providers.Find(payment.Provider)
            ?? throw new InvalidOperationException("The provider that took this payment is not registered in this host.");
        ProviderPaymentResult? answer;
        try
        {
            answer = await provider.GetStatusAsync(new ProviderStatusQuery(payment.Id, payment.TenantId, payment.ProviderPaymentId, payment.PaymentMethod), cancellationToken);
        }
        catch (PaymentProviderUnavailableException)
        {
            answer = null;
        }

        if (answer is { Outcome: PaymentTrigger.Succeeded or PaymentTrigger.Failed })
        {
            await store.ApplyAsync(payment.Id, answer.Outcome, answer.ProviderPaymentId, answer.FailureCode, cancellationToken);
            return TimerDecision.Complete;
        }

        var now = clock.GetUtcNow();
        if (payment.Status == PaymentStatus.RequiresReview)
        {
            return TimerDecision.RunAgain(now + PaymentReconciliation.ReviewInterval);
        }

        if (checksSoFar + 1 >= PaymentReconciliation.MaxChecks)
        {
            await store.ApplyAsync(payment.Id, PaymentTrigger.ReviewRequired, answer?.ProviderPaymentId, null, cancellationToken);
            return TimerDecision.RunAgain(now + PaymentReconciliation.ReviewInterval);
        }

        return TimerDecision.RunAgain(now + PaymentReconciliation.Interval);
    }
}
