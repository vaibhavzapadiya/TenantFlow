using TenantFlow.Orders.Application.Abstractions;
using TenantFlow.Orders.Domain.Orders;

namespace TenantFlow.Orders.Application.Orders.Dtos;

/// <summary>An order as the use cases return it: the status by its stable name, and the row version for the ETag.</summary>
public sealed record OrderDetails(
    Guid Id,
    string Status,
    string Currency,
    decimal TotalAmount,
    IReadOnlyList<OrderLineDetails> Lines,
    string? RejectionReason,
    DateTimeOffset CreatedAt,
    uint Version)
{
    internal static OrderDetails From(OrderRecord record)
    {
        var order = record.Order;
        return new OrderDetails(
            order.Id,
            order.Status.ToName(),
            order.Currency,
            order.TotalAmount,
            [.. order.Lines.OrderBy(static l => l.LineNo).Select(static l => new OrderLineDetails(l.ProductId, l.Sku, l.Name, l.UnitPrice, l.Quantity, l.LineTotal))],
            order.RejectionReason,
            order.CreatedAt,
            record.Version);
    }
}
