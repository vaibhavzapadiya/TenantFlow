namespace TenantFlow.BuildingBlocks.Abstractions.Messaging;

/// <summary>
/// A delivered integration event with its envelope already validated (ADR-0021): a message id, a type, a version and a
/// well-formed tenant id. Whether the tenant exists and the payload is readable is for the consumer to decide.
/// </summary>
public sealed record IncomingMessage(
    Guid MessageId,
    string Type,
    int Version,
    Guid TenantId,
    string Payload,
    string? CorrelationId,
    string? TraceParent);
