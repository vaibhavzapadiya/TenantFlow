using TenantFlow.BuildingBlocks.Abstractions.Errors;

namespace TenantFlow.Audit.Domain.Records;

/// <summary>
/// The Audit module's error catalog (ADR-0016). Codes are a public contract: never rename or reuse one.
/// Messages are fixed sentences without user input.
/// </summary>
public static class AuditErrors
{
    public static readonly AppError CursorInvalid =
        new("audit.cursor_invalid", ErrorKind.Validation, "The cursor is not valid.");

    public static readonly AppError LimitInvalid =
        new("audit.limit_invalid", ErrorKind.Validation, "The limit must be between 1 and 100.");

    public static readonly AppError ActionInvalid =
        new("audit.action_invalid", ErrorKind.Validation, "The action is not an audited action.");

    public static readonly AppError TenantRequired =
        new("audit.tenant_required", ErrorKind.Validation, "A tenantId query parameter with a tenant id is required.");
}
