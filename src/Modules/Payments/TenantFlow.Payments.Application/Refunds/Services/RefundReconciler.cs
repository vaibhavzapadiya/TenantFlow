using TenantFlow.BuildingBlocks.Abstractions.Workflow;
using TenantFlow.Payments.Application.Abstractions;
using TenantFlow.Payments.Application.Payments.Services;
using TenantFlow.Payments.Domain.Payments;

namespace TenantFlow.Payments.Application.Refunds.Services;

/// <summary>
/// Settles refunds whose outcome is not known yet (SPEC-0007 criterion 12), on the payment reconciliation schedule: asks
/// the provider that took the payment, outside any transaction, and applies a final answer like a webhook. It never sends
/// a refund again. After <see cref="PaymentReconciliation.MaxChecks"/> checks without a final answer the refund needs
/// review; checks then continue more slowly.
/// </summary>
public sealed class RefundReconciler(IPaymentStore store, PaymentProviders providers, TimeProvider clock)
{
    /// <param name="refundId">The timer's subject.</param>
    /// <param name="checksSoFar">How often this refund was checked before (the timer's attempts).</param>
    /// <param name="cancellationToken">Stops the check; the timer's lease brings it back.</param>
    public async Task<TimerDecision> ReconcileAsync(Guid refundId, int checksSoFar, CancellationToken cancellationToken)
    {
        var refund = await store.FindRefundAsync(refundId, cancellationToken);
        if (refund is null || refund.Status is PaymentStatus.Succeeded or PaymentStatus.Failed)
        {
            return TimerDecision.Complete;
        }

        var payment = await store.FindAsync(refund.PaymentId, cancellationToken)
            ?? throw new InvalidOperationException("The refunded payment does not exist in this tenant.");
        var provider = providers.Find(refund.Provider)
            ?? throw new InvalidOperationException("The provider that took this payment is not registered in this host.");
        ProviderRefundResult? answer;
        try
        {
            answer = await provider.GetRefundStatusAsync(
                new ProviderRefundQuery(refund.Id, refund.TenantId, payment.ProviderPaymentId, refund.ProviderRefundId, payment.PaymentMethod), cancellationToken);
        }
        catch (PaymentProviderUnavailableException)
        {
            answer = null;
        }

        if (answer is { Outcome: PaymentTrigger.Succeeded or PaymentTrigger.Failed })
        {
            await store.ApplyRefundAsync(refund.Id, answer.Outcome, answer.ProviderRefundId, answer.FailureCode, cancellationToken);
            return TimerDecision.Complete;
        }

        var now = clock.GetUtcNow();
        if (refund.Status == PaymentStatus.RequiresReview)
        {
            return TimerDecision.RunAgain(now + PaymentReconciliation.ReviewInterval);
        }

        if (checksSoFar + 1 >= PaymentReconciliation.MaxChecks)
        {
            await store.ApplyRefundAsync(refund.Id, PaymentTrigger.ReviewRequired, answer?.ProviderRefundId, null, cancellationToken);
            return TimerDecision.RunAgain(now + PaymentReconciliation.ReviewInterval);
        }

        return TimerDecision.RunAgain(now + PaymentReconciliation.Interval);
    }
}
