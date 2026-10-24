using TenantFlow.Audit.Domain.Records;

namespace TenantFlow.Audit.Application.Records.Dtos;

/// <summary>One audit record as callers see it (SPEC-0009 Contract): codes and ids only.</summary>
public sealed record AuditRecordDetails(Guid Id, DateTimeOffset OccurredAt, string ActorKind, string ActorRef, string Action, string TargetType, Guid TargetId, string? TraceId)
{
    public static AuditRecordDetails From(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new(record.Id, record.OccurredAt, record.ActorKind, record.ActorRef, record.Action, record.TargetType, record.TargetId, record.TraceId);
    }
}

/// <summary>One page of a tenant's audit log, newest first; <c>NextCursor</c> is null on the last page.</summary>
public sealed record AuditPage(IReadOnlyList<AuditRecordDetails> Items, string? NextCursor);
