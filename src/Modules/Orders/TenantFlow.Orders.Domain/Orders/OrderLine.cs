namespace TenantFlow.Orders.Domain.Orders;

/// <summary>
/// One product in an order, frozen at the price, currency, SKU and name the product had when the line was set
/// (SPEC-0004 criterion 6). The currency is the order's. Lines are replaced as a whole, never edited one by one.
/// </summary>
public sealed class OrderLine
{
    private OrderLine()
    {
    }

    public long Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid OrderId { get; private set; }

    /// <summary>The line's position in the order, from 1, in the order the caller listed the lines.</summary>
    public int LineNo { get; private set; }

    public Guid ProductId { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public decimal UnitPrice { get; private set; }

    public int Quantity { get; private set; }

    public decimal LineTotal { get; private set; }

    internal static OrderLine From(Guid tenantId, Guid orderId, int lineNo, OrderLineDraft draft) => new()
    {
        TenantId = tenantId,
        OrderId = orderId,
        LineNo = lineNo,
        ProductId = draft.ProductId,
        Sku = draft.Sku,
        Name = draft.Name,
        UnitPrice = draft.UnitPrice,
        Quantity = draft.Quantity,
        LineTotal = draft.UnitPrice * draft.Quantity,
    };
}
