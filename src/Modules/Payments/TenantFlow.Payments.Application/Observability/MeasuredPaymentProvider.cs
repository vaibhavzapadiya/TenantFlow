using System.Diagnostics;
using TenantFlow.Payments.Application.Abstractions;

namespace TenantFlow.Payments.Application.Observability;

/// <summary>
/// Times every call to a payment provider adapter (SPEC-0010 criterion 4: provider call latency) and records it in
/// <see cref="PaymentsMetrics"/> by provider, operation and outcome: <c>answered</c>, <c>unavailable</c> (no usable answer,
/// the outcome is unknown) or <c>error</c>. Behaviour is unchanged: results and exceptions pass through.
/// </summary>
public sealed class MeasuredPaymentProvider(IPaymentProvider inner, PaymentsMetrics metrics) : IPaymentProvider
{
    public const string CreateOperation = "create";
    public const string StatusOperation = "status";
    public const string RefundOperation = "refund";
    public const string RefundStatusOperation = "refund_status";

    public string Name => inner.Name;

    public Task<ProviderPaymentResult> CreateAsync(ProviderPaymentRequest request, CancellationToken cancellationToken) =>
        MeasureAsync(CreateOperation, () => inner.CreateAsync(request, cancellationToken));

    public Task<ProviderPaymentResult?> GetStatusAsync(ProviderStatusQuery query, CancellationToken cancellationToken) =>
        MeasureAsync(StatusOperation, () => inner.GetStatusAsync(query, cancellationToken));

    public Task<ProviderRefundResult> RefundAsync(ProviderRefundRequest request, CancellationToken cancellationToken) =>
        MeasureAsync(RefundOperation, () => inner.RefundAsync(request, cancellationToken));

    public Task<ProviderRefundResult?> GetRefundStatusAsync(ProviderRefundQuery query, CancellationToken cancellationToken) =>
        MeasureAsync(RefundStatusOperation, () => inner.GetRefundStatusAsync(query, cancellationToken));

    private async Task<T> MeasureAsync<T>(string operation, Func<Task<T>> call)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var outcome = PaymentsMetrics.Error;
        try
        {
            var result = await call();
            outcome = PaymentsMetrics.Answered;
            return result;
        }
        catch (PaymentProviderUnavailableException)
        {
            outcome = PaymentsMetrics.Unavailable;
            throw;
        }
        finally
        {
            metrics.RecordProviderCall(inner.Name, operation, outcome, startedAt);
        }
    }
}
