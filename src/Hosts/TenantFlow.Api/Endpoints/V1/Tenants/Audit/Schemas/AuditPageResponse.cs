using TenantFlow.Audit.Application.Records.Dtos;

namespace TenantFlow.Api.Endpoints.V1.Tenants.Audit.Schemas;

/// <summary>One audit record (SPEC-0009 Contract): codes and ids only, never a name, subject or free text.</summary>
internal sealed record AuditRecordResponse(Guid Id, DateTimeOffset OccurredAt, string ActorKind, string ActorRef, string Action, string TargetType, Guid TargetId, string? TraceId)
{
    public static AuditRecordResponse From(AuditRecordDetails record) =>
        new(record.Id, record.OccurredAt, record.ActorKind, record.ActorRef, record.Action, record.TargetType, record.TargetId, record.TraceId);
}

/// <summary>One page of an audit log, newest first; <c>nextCursor</c> is null on the last page (ADR-0019 keyset paging).</summary>
internal sealed record AuditPageResponse(IReadOnlyList<AuditRecordResponse> Items, string? NextCursor)
{
    public static AuditPageResponse From(AuditPage page) => new([.. page.Items.Select(AuditRecordResponse.From)], page.NextCursor);
}
