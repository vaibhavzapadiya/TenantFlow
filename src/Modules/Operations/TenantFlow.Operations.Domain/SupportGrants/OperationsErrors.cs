using TenantFlow.BuildingBlocks.Abstractions.Errors;

namespace TenantFlow.Operations.Domain.SupportGrants;

/// <summary>
/// The Operations module's error catalog (ADR-0016). Codes are a public contract: never rename or reuse one.
/// Messages are fixed sentences without user input.
/// </summary>
public static class OperationsErrors
{
    public static readonly AppError TenantRequired =
        new("support.tenant_required", ErrorKind.Validation, "A tenantId is required.");

    public static readonly AppError TicketInvalid =
        new("support.ticket_invalid", ErrorKind.Validation, "The ticket must be a reference such as SUP-1234.");

    public static readonly AppError DurationInvalid =
        new("support.duration_invalid", ErrorKind.Validation, "The duration must be between 1 and 240 minutes.");

    public static readonly AppError GrantExists =
        new("support.grant_exists", ErrorKind.Conflict, "You already have an active support grant for this tenant.");

    public static readonly AppError GrantNotFound =
        new("support.grant_not_found", ErrorKind.NotFound, "The support grant does not exist or is no longer active.");
}
