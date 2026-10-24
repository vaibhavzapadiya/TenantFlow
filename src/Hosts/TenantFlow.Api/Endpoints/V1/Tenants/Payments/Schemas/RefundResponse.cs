using TenantFlow.Api.Endpoints.V1.Tenants.Orders.Schemas;
using TenantFlow.Payments.Application.Refunds.Dtos;

namespace TenantFlow.Api.Endpoints.V1.Tenants.Payments.Schemas;

/// <summary>
/// A refund as the API returns it, with its audit fields. Status is <c>processing</c>, <c>succeeded</c>, <c>failed</c> or
/// <c>requires_review</c>; kind is <c>refund</c> or <c>cancellation</c>.
/// </summary>
internal sealed record RefundResponse(Guid Id, Guid PaymentId, Guid OrderId, string Status, string Kind, MoneyResponse Amount, string Reason, Guid RequestedByMembershipId, DateTimeOffset CreatedAt)
{
    public static RefundResponse From(RefundDetails refund) =>
        new(refund.Id, refund.PaymentId, refund.OrderId ?? throw new InvalidOperationException("Only order refunds are served by this API."), refund.Status, refund.Kind, new MoneyResponse(refund.Amount, refund.Currency), refund.Reason,
            refund.RequestedByMembershipId, refund.CreatedAt);
}

/// <summary>The refunds of an order's payment, oldest first.</summary>
internal sealed record RefundListResponse(IReadOnlyList<RefundResponse> Items);
