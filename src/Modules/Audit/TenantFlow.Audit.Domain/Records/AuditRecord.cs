using TenantFlow.Audit.Contracts;

namespace TenantFlow.Audit.Domain.Records;

/// <summary>
/// One entry of the append-only audit log (SPEC-0009 criteria 2 and 3, ADR-0026): what was done, by whom (a reference,
/// never a subject, name or email), to which target, in which tenant. Its id is the id of the message that carried it,
/// so a redelivered message can never make a second record. Never changed after it is written.
/// </summary>
public sealed class AuditRecord
{
    /// <summary>The length of a W3C trace id in hex.</summary>
    public const int TraceIdLength = 32;

    private AuditRecord(Guid id, Guid tenantId, DateTimeOffset occurredAt, string actorKind, string actorRef, string action, string targetType, Guid targetId, string? traceId)
    {
        Id = id;
        TenantId = tenantId;
        OccurredAt = occurredAt;
        ActorKind = actorKind;
        ActorRef = actorRef;
        Action = action;
        TargetType = targetType;
        TargetId = targetId;
        TraceId = traceId;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>One of <see cref="AuditActorKinds"/>.</summary>
    public string ActorKind { get; private set; }

    public string ActorRef { get; private set; }

    /// <summary>One of <see cref="AuditActions"/>.</summary>
    public string Action { get; private set; }

    /// <summary>One of <see cref="AuditTargetTypes"/>.</summary>
    public string TargetType { get; private set; }

    public Guid TargetId { get; private set; }

    /// <summary>The trace of the request that made the change, so an entry leads to its logs; null when there was none.</summary>
    public string? TraceId { get; private set; }

    /// <summary>
    /// True when every part is from the closed lists and in the shape its kind requires, the time is a set UTC instant,
    /// and a tenant target is the record's own tenant, so one tenant's log can never name another tenant.
    /// </summary>
    public static bool IsValid(Guid id, Guid tenantId, DateTimeOffset occurredAt, string? actorKind, string? actorRef, string? action, string? targetType, Guid targetId, string? traceId) =>
        id != Guid.Empty
        && tenantId != Guid.Empty
        && targetId != Guid.Empty
        // A missing time reads as year 1 and would be purged at once; producers write UTC (TimeProvider).
        && occurredAt != default && occurredAt.Offset == TimeSpan.Zero
        && action is not null && AuditActions.All.Contains(action)
        && targetType is not null && AuditTargetTypes.All.Contains(targetType)
        && (targetType != AuditTargetTypes.Tenant || targetId == tenantId)
        && AuditActor.IsValid(actorKind, actorRef)
        && (traceId is null || (traceId.Length == TraceIdLength && traceId.All(static c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f')));

    /// <exception cref="ArgumentException">A part that <see cref="IsValid"/> refuses: a programming error.</exception>
    public static AuditRecord Create(Guid id, Guid tenantId, DateTimeOffset occurredAt, string actorKind, string actorRef, string action, string targetType, Guid targetId, string? traceId) =>
        IsValid(id, tenantId, occurredAt, actorKind, actorRef, action, targetType, targetId, traceId)
            ? new AuditRecord(id, tenantId, occurredAt, actorKind, actorRef, action, targetType, targetId, traceId)
            : throw new ArgumentException("An audit record needs ids, known codes and a reference in the shape of its actor kind.");
}
