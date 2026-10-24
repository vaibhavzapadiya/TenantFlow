using TenantFlow.BuildingBlocks.Abstractions.Messaging;

namespace TenantFlow.Audit.Contracts.Events;

/// <summary>
/// An audited action, written by the module that performed it to its own outbox in the same transaction as the change
/// (ADR-0026 decision 2). The tenant, the message id (which becomes the record's id) and the trace come from the
/// envelope. The payload holds codes and ids only: no subject, name, email or free text.
/// </summary>
/// <param name="Action">One of <see cref="AuditActions"/>.</param>
/// <param name="ActorKind">One of <see cref="AuditActorKinds"/>.</param>
/// <param name="ActorRef">The actor's reference, in the shape its kind requires (<see cref="AuditActor"/>).</param>
/// <param name="TargetType">One of <see cref="AuditTargetTypes"/>.</param>
/// <param name="TargetId">The id of what was acted on.</param>
/// <param name="OccurredAt">When the change was made (UTC).</param>
public sealed record AuditRecordedV1(string Action, string ActorKind, string ActorRef, string TargetType, Guid TargetId, DateTimeOffset OccurredAt) : IIntegrationEvent
{
    public static string EventType => "audit.recorded.v1";

    public static int EventVersion => 1;

    /// <summary>The event for <paramref name="action"/> by <paramref name="actor"/> on a target.</summary>
    public static AuditRecordedV1 Of(string action, AuditActor actor, string targetType, Guid targetId, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!AuditActions.All.Contains(action) || !AuditTargetTypes.All.Contains(targetType) || targetId == Guid.Empty)
        {
            // A programming error: the domain builds events from the closed lists only.
            throw new ArgumentException("An audit event needs a known action, a known target type and a target id.");
        }

        return new AuditRecordedV1(action, actor.Kind, actor.Reference, targetType, targetId, occurredAt);
    }
}
