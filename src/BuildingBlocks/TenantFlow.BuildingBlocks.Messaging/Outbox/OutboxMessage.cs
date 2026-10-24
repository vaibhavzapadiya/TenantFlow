using System.Diagnostics;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;

namespace TenantFlow.BuildingBlocks.Messaging.Outbox;

/// <summary>
/// One row of a module's outbox: an integration event and its envelope, written in the same transaction as the state
/// change it announces (ADR-0021). The id is the message id the broker and every consumer see, so it never changes.
/// </summary>
public sealed class OutboxMessage
{
    private OutboxMessage(Guid id, Guid tenantId, string type, int version, string payload, string correlationId, string? traceParent, DateTimeOffset occurredAt)
    {
        Id = id;
        TenantId = tenantId;
        Type = type;
        Version = version;
        Payload = payload;
        CorrelationId = correlationId;
        TraceParent = traceParent;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Type { get; private set; }

    public int Version { get; private set; }

    /// <summary>The event as JSON (<c>jsonb</c>).</summary>
    public string Payload { get; private set; }

    public string CorrelationId { get; private set; }

    /// <summary>The W3C <c>traceparent</c> of the activity that caused the event, so the consumer joins the same trace.</summary>
    public string? TraceParent { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>Set while a publisher holds the row; once it passes, another publisher may claim the row again.</summary>
    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>
    /// The row for <paramref name="message"/> in <paramref name="tenantId"/>. The trace context comes from the current
    /// activity; the correlation id is its trace id, or the message id when there is none.
    /// </summary>
    /// <exception cref="ArgumentException">The tenant id is empty: a programming error.</exception>
    public static OutboxMessage Create(OutgoingMessage message, Guid tenantId, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("An outbox message needs a tenant id.", nameof(tenantId));
        }

        var id = Guid.CreateVersion7(occurredAt);
        var activity = Activity.Current;
        var traceParent = activity?.IdFormat == ActivityIdFormat.W3C ? activity.Id : null;
        var correlationId = traceParent is null ? id.ToString("N") : activity!.TraceId.ToHexString();
        return new OutboxMessage(id, tenantId, message.Type, message.Version, message.Payload, correlationId, traceParent, occurredAt);
    }
}
