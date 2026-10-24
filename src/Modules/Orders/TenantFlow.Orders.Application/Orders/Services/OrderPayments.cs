using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.Orders.Application.Abstractions;
using TenantFlow.Orders.Contracts;
using TenantFlow.Orders.Domain.Orders;

namespace TenantFlow.Orders.Application.Orders.Services;

/// <summary>
/// The Orders side of paying an order (SPEC-0006 criteria 1, 3, 4 and 6). Only the order's owner may pay it, and to anyone
/// else it does not exist; ownership is checked before anything is written, so nobody else can block an order's cancel.
/// The begin mark comes after Payments has committed the payment (review fix), so a marked order always has a payment that
/// reconciliation settles, and the mark is one conditional update, so it and a cancel or an expiry cannot both win.
/// </summary>
public sealed class OrderPayments(ITenantContext context, IOrderStore store) : IOrderPayments
{
    public async Task<PayableOrder> GetPayableAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await FindOwnAsync(orderId, cancellationToken);
        return order.Status == OrderStatus.PendingPayment
            ? new PayableOrder(order.Id, order.TotalAmount, order.Currency)
            : throw new AppException(OrderErrors.InvalidTransition);
    }

    public async Task<bool> BeginPaymentAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await FindOwnAsync(orderId, cancellationToken);
        if (order.Status == OrderStatus.PendingPayment && order.PaymentStartedAt is not null)
        {
            return true;
        }

        if (await store.TryBeginPaymentAsync(orderId, cancellationToken))
        {
            return true;
        }

        // Moved between the read and the write: begun by a concurrent request (fine), or cancelled or expired (not payable).
        var now = (await store.FindAsync(orderId, cancellationToken))?.Order;
        return now is { Status: OrderStatus.PendingPayment, PaymentStartedAt: not null };
    }

    private async Task<Order> FindOwnAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var record = await store.FindAsync(orderId, cancellationToken);
        return record is not null && record.Order.IsOwnedBy(context.MembershipId) ? record.Order : throw new AppException(OrderErrors.NotFound);
    }
}
