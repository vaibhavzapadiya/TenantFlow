using System.Text.Json;
using TenantFlow.Audit.Application.Abstractions;
using TenantFlow.Audit.Contracts.Events;
using TenantFlow.Audit.Domain.Records;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.Tenants.Contracts;

namespace TenantFlow.Audit.Application.Records.Services;

/// <summary>
/// Records every <c>audit.recorded.v1</c> event in the audit log (SPEC-0009 criteria 2 and 6, ADR-0026). The record's id
/// is the message id and its tenant the envelope's, which must exist; anything else in the payload must come from the
/// closed lists. A message that can never be recorded throws <see cref="InvalidMessageException"/> and is dead-lettered.
/// Repeats of a message id change nothing.
/// </summary>
public sealed class AuditEventConsumer(IAuditStore store, ITenantDirectory tenants)
{
    /// <summary>This consumer's name in <c>processed_messages</c>, and its queue.</summary>
    public const string Consumer = "audit.audit-events";

    /// <summary>The event types this consumer subscribes to, bound by name.</summary>
    public static readonly IReadOnlyList<string> EventTypes = [AuditRecordedV1.EventType];

    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    /// <exception cref="InvalidMessageException">Unknown type, version or tenant, or an unreadable or invalid payload.</exception>
    public async Task<MessageOutcome> HandleAsync(IncomingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Type != AuditRecordedV1.EventType)
        {
            throw new InvalidMessageException("Unknown message type.");
        }

        if (message.Version != AuditRecordedV1.EventVersion)
        {
            throw new InvalidMessageException("Unsupported message version.");
        }

        var payload = Read(message.Payload);
        var traceId = TraceIdOf(message.TraceParent);
        if (!AuditRecord.IsValid(message.MessageId, message.TenantId, payload.OccurredAt, payload.ActorKind, payload.ActorRef, payload.Action, payload.TargetType, payload.TargetId, traceId))
        {
            // The reason never quotes the payload: it may be a forgery carrying text that must not reach a log.
            throw new InvalidMessageException("Invalid audit event.");
        }

        // The envelope's tenant is well formed; it must also exist before anything is written for it.
        var known = await tenants.GetAvailabilityAsync([message.TenantId], cancellationToken);
        if (!known.ContainsKey(message.TenantId))
        {
            throw new InvalidMessageException("Unknown tenant.");
        }

        var record = AuditRecord.Create(
            message.MessageId, message.TenantId, payload.OccurredAt, payload.ActorKind, payload.ActorRef, payload.Action, payload.TargetType, payload.TargetId, traceId);
        return await store.RecordAsync(record, Consumer, cancellationToken) ? MessageOutcome.Processed : MessageOutcome.Duplicate;
    }

    private static AuditRecordedV1 Read(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<AuditRecordedV1>(payload, PayloadOptions) ?? throw new InvalidMessageException("Unreadable payload.");
        }
        catch (JsonException exception)
        {
            // The parser's message can quote payload text; the reason stays generic.
            throw new InvalidMessageException("Unreadable payload.", exception);
        }
    }

    // "00-{trace id}-{span id}-{flags}": the trace id alone, or null when the change was made outside any trace.
    private static string? TraceIdOf(string? traceParent)
    {
        var parts = traceParent?.Split('-');
        return parts is { Length: 4 } && parts[1].Length == AuditRecord.TraceIdLength ? parts[1] : null;
    }
}
