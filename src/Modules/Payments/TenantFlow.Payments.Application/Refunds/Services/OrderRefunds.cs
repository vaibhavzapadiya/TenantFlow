using TenantFlow.Payments.Application.Abstractions;
using TenantFlow.Payments.Contracts;

namespace TenantFlow.Payments.Application.Refunds.Services;

/// <summary>
/// The cancellation refund of a paid order (SPEC-0007 criterion 14, ADR-0023 decision 5), asked by the Orders module's
/// cancellation job in the order's tenant: the refund of what is left of the order's payment (<see cref="RemainderRefunds"/>).
/// </summary>
public sealed class OrderRefunds(IPaymentStore store, RemainderRefunds remainder) : IOrderRefunds
{
    public async Task<CancellationRefund> RefundRemainderAsync(Guid orderId, string reason, Guid requestedByMembershipId, CancellationToken cancellationToken)
    {
        var payment = await store.FindByOrderAsync(orderId, cancellationToken);
        return payment is null
            ? CancellationRefund.NotRefundable
            : await remainder.RefundAsync(payment, reason, requestedByMembershipId, cancellationToken) switch
            {
                RemainderRefund.Requested => CancellationRefund.Requested,
                RemainderRefund.NothingLeft => CancellationRefund.NothingLeft,
                RemainderRefund.Waiting => CancellationRefund.Waiting,
                _ => CancellationRefund.NotRefundable,
            };
    }
}
