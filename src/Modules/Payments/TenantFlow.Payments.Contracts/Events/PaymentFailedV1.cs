using TenantFlow.BuildingBlocks.Abstractions.Messaging;

namespace TenantFlow.Payments.Contracts.Events;

/// <summary>Published once when a payment moves to <c>failed</c>; <see cref="FailureCode"/> is the provider's short reason.</summary>
public sealed record PaymentFailedV1(Guid PaymentId, Guid OrderId, PaymentAmount Amount, string? FailureCode) : IIntegrationEvent
{
    public static string EventType => "payment.failed.v1";

    public static int EventVersion => 1;
}
