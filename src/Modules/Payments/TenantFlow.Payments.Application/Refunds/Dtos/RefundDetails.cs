using TenantFlow.Payments.Domain.Payments;
using TenantFlow.Payments.Domain.Refunds;

namespace TenantFlow.Payments.Application.Refunds.Dtos;

/// <summary>
/// A refund as callers see it, with its audit fields. An unknown outcome reads <c>processing</c>, as for payments. This is
/// also what an idempotency key replays.
/// </summary>
public sealed record RefundDetails(
    Guid Id,
    Guid PaymentId,
    Guid? OrderId,
    string Status,
    string Kind,
    decimal Amount,
    string Currency,
    string Reason,
    Guid RequestedByMembershipId,
    DateTimeOffset CreatedAt)
{
    public static RefundDetails From(Refund refund)
    {
        ArgumentNullException.ThrowIfNull(refund);
        var status = refund.Status == PaymentStatus.Unknown ? PaymentStatusNames.Processing : refund.Status.ToName();
        return new RefundDetails(refund.Id, refund.PaymentId, refund.OrderId, status, refund.Kind, refund.Amount, refund.Currency, refund.Reason,
            refund.RequestedByMembershipId, refund.CreatedAt);
    }
}
