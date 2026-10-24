using TenantFlow.Orders.Application.Orders.Dtos;

namespace TenantFlow.Api.Endpoints.V1.Tenants.Orders.Schemas;

/// <summary>An order as the API returns it; its version travels in the <c>ETag</c> header, not the body.</summary>
internal sealed record OrderResponse(
    Guid Id,
    string Status,
    MoneyResponse Total,
    IReadOnlyList<OrderLineResponse> Lines,
    string? RejectionReason,
    DateTimeOffset CreatedAt)
{
    public static OrderResponse From(OrderDetails order) => new(
        order.Id,
        order.Status,
        new MoneyResponse(order.TotalAmount, order.Currency),
        [.. order.Lines.Select(l => new OrderLineResponse(
            l.ProductId, l.Sku, l.Name, new MoneyResponse(l.UnitPrice, order.Currency), l.Quantity, new MoneyResponse(l.LineTotal, order.Currency)))],
        order.RejectionReason,
        order.CreatedAt);
}
