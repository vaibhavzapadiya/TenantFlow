namespace TenantFlow.BuildingBlocks.Messaging.Outbox;

/// <summary>An outbox row a publisher has claimed: everything the broker message needs (ADR-0021 envelope).</summary>
public sealed record PendingMessage(
    Guid Id,
    Guid TenantId,
    string Type,
    int Version,
    string Payload,
    string CorrelationId,
    string? TraceParent,
    DateTimeOffset OccurredAt);
