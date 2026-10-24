namespace TenantFlow.Audit.Application.Abstractions;

/// <summary>A keyset position: the last record of the previous page, by (occurred at, id), newest first.</summary>
public sealed record AuditCursor(DateTimeOffset OccurredAt, Guid Id);
