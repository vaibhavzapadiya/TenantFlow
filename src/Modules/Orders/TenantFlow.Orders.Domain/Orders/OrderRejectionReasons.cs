namespace TenantFlow.Orders.Domain.Orders;

/// <summary>Why an order was rejected, by stable name: stored and returned as is, so never rename one.</summary>
public static class OrderRejectionReasons
{
    public const string InsufficientStock = "insufficient_stock";
}
