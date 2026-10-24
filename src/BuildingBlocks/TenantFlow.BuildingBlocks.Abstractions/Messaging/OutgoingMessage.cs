using System.Text.Json;

namespace TenantFlow.BuildingBlocks.Abstractions.Messaging;

/// <summary>
/// An integration event ready for a module's outbox: its type, version and JSON payload. The store that writes it adds
/// the message id, tenant, time and trace context in the transaction of the state change (ADR-0021).
/// </summary>
public sealed record OutgoingMessage(string Type, int Version, string Payload)
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    public static OutgoingMessage From<TEvent>(TEvent integrationEvent)
        where TEvent : IIntegrationEvent =>
        new(TEvent.EventType, TEvent.EventVersion, JsonSerializer.Serialize(integrationEvent, PayloadOptions));
}
