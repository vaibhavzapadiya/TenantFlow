namespace TenantFlow.BuildingBlocks.Abstractions.Messaging;

/// <summary>
/// An event one module publishes for others through the outbox (ADR-0021). The payload carries identifiers, statuses
/// and amounts only, never personal data. A breaking change is a new type with a new version, never an edit.
/// </summary>
public interface IIntegrationEvent
{
    /// <summary>The routing key and stable name, for example <c>order.reserved.v1</c>.</summary>
    static abstract string EventType { get; }

    static abstract int EventVersion { get; }
}
