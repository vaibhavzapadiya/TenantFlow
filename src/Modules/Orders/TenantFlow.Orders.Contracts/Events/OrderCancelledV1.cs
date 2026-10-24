using TenantFlow.BuildingBlocks.Abstractions.Messaging;

namespace TenantFlow.Orders.Contracts.Events;

/// <summary>
/// Published when an order moves to <c>cancelled</c> (SPEC-0005 criterion 1). <see cref="PreviousStatus"/> is the stable
/// status name it left, for example <c>pending_payment</c>.
/// </summary>
public sealed record OrderCancelledV1(Guid OrderId, Guid OwnerMembershipId, string PreviousStatus) : IIntegrationEvent
{
    public static string EventType => "order.cancelled.v1";

    public static int EventVersion => 1;
}
