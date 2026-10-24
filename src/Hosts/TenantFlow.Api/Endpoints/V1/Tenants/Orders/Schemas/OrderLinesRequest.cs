using TenantFlow.Orders.Application.Orders.Commands;

namespace TenantFlow.Api.Endpoints.V1.Tenants.Orders.Schemas;

/// <summary>
/// Body of <c>POST /tenants/{tenantId}/orders</c> and <c>PUT /tenants/{tenantId}/orders/{orderId}/lines</c>.
/// Rules: <c>OrderLinesCommandValidator</c> (ADR-0014).
/// </summary>
internal sealed record OrderLinesRequest(IReadOnlyList<OrderLineRequest?>? Lines)
{
    public OrderLinesCommand ToCommand() =>
        new(Lines?.Select(static l => l is null ? null : new OrderLineInput(l.ProductId, l.Quantity)).ToList());
}
