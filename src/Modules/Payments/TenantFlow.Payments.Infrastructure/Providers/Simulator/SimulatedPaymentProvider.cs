using TenantFlow.Payments.Application.Abstractions;
using TenantFlow.Payments.Domain.Payments;

namespace TenantFlow.Payments.Infrastructure.Providers.Simulator;

/// <summary>
/// A deterministic in-process payment provider (ADR-0022 decision 1). Its answer is a pure function of the payment-method
/// token and the payment id, so the API and the Worker agree without sharing memory, and a repeated request gets the
/// same provider id: the payment id is the idempotency key, as with a real provider. Refunds work the same way, by the
/// refunded payment's token and the refund id (SPEC-0007).
/// </summary>
internal sealed class SimulatedPaymentProvider : IPaymentProvider
{
    public const string ProviderName = "simulator";

    public string Name => ProviderName;

    public Task<ProviderPaymentResult> CreateAsync(ProviderPaymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var providerId = ProviderPaymentId(request.PaymentId);
        return request.PaymentMethod switch
        {
            SimulatorPaymentMethods.Success or SimulatorPaymentMethods.RefundDecline or SimulatorPaymentMethods.RefundTimeout =>
                Task.FromResult(new ProviderPaymentResult(PaymentTrigger.Succeeded, providerId)),
            SimulatorPaymentMethods.Decline => Task.FromResult(new ProviderPaymentResult(PaymentTrigger.Failed, providerId, "card_declined")),
            SimulatorPaymentMethods.TimeoutThenSuccess or SimulatorPaymentMethods.TimeoutThenDecline or SimulatorPaymentMethods.TimeoutUnknown =>
                throw new PaymentProviderUnavailableException("The simulated provider timed out."),
            SimulatorPaymentMethods.ServerError => throw new PaymentProviderUnavailableException("The simulated provider answered 500."),
            _ => Task.FromResult(new ProviderPaymentResult(PaymentTrigger.Failed, providerId, "payment_method_unsupported")),
        };
    }

    public Task<ProviderPaymentResult?> GetStatusAsync(ProviderStatusQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var providerId = ProviderPaymentId(query.PaymentId);
        // What the provider knows afterwards: a timed-out charge has an outcome, a 5xx left no record at all.
        return Task.FromResult<ProviderPaymentResult?>(query.PaymentMethod switch
        {
            SimulatorPaymentMethods.Success or SimulatorPaymentMethods.TimeoutThenSuccess or SimulatorPaymentMethods.RefundDecline or SimulatorPaymentMethods.RefundTimeout =>
                new ProviderPaymentResult(PaymentTrigger.Succeeded, providerId),
            SimulatorPaymentMethods.Decline or SimulatorPaymentMethods.TimeoutThenDecline => new ProviderPaymentResult(PaymentTrigger.Failed, providerId, "card_declined"),
            SimulatorPaymentMethods.TimeoutUnknown => new ProviderPaymentResult(PaymentTrigger.Pending, providerId),
            SimulatorPaymentMethods.ServerError => null,
            _ => new ProviderPaymentResult(PaymentTrigger.Failed, providerId, "payment_method_unsupported"),
        });
    }

    public Task<ProviderRefundResult> RefundAsync(ProviderRefundRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var providerId = ProviderRefundId(request.RefundId);
        return request.PaymentMethod switch
        {
            SimulatorPaymentMethods.RefundDecline => Task.FromResult(new ProviderRefundResult(PaymentTrigger.Failed, providerId, "refund_declined")),
            SimulatorPaymentMethods.RefundTimeout => throw new PaymentProviderUnavailableException("The simulated provider timed out."),
            _ => Task.FromResult(new ProviderRefundResult(PaymentTrigger.Succeeded, providerId)),
        };
    }

    public Task<ProviderRefundResult?> GetRefundStatusAsync(ProviderRefundQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var providerId = ProviderRefundId(query.RefundId);
        return Task.FromResult<ProviderRefundResult?>(query.PaymentMethod == SimulatorPaymentMethods.RefundDecline
            ? new ProviderRefundResult(PaymentTrigger.Failed, providerId, "refund_declined")
            : new ProviderRefundResult(PaymentTrigger.Succeeded, providerId));
    }

    /// <summary>The provider's id for a refund: derived, so every process computes the same one.</summary>
    public static string ProviderRefundId(Guid refundId) => $"sim_re_{refundId:N}";

    /// <summary>The provider's id for a payment: derived, so every process computes the same one.</summary>
    public static string ProviderPaymentId(Guid paymentId) => $"sim_pi_{paymentId:N}";
}
