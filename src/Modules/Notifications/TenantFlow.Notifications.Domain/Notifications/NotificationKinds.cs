namespace TenantFlow.Notifications.Domain.Notifications;

/// <summary>The stable names of notification kinds, as stored and checked by the database.</summary>
public static class NotificationKinds
{
    public const string OrderReserved = "order_reserved";
    public const string OrderCancelled = "order_cancelled";

    public static readonly IReadOnlyList<string> All = [OrderReserved, OrderCancelled];
}
