using TenantFlow.BuildingBlocks.Abstractions.Messaging;

namespace TenantFlow.Orders.Contracts.Events;

/// <summary>Published when an order's stock is reserved and it moves to <c>pending_payment</c> (SPEC-0005 criterion 1).</summary>
public sealed record OrderReservedV1(Guid OrderId, Guid OwnerMembershipId, OrderTotal Total, int LineCount) : IIntegrationEvent
{
    public static string EventType => "order.reserved.v1";

    public static int EventVersion => 1;
}
