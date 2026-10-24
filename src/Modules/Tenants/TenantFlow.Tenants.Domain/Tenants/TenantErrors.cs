using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.Tenants.Contracts;

namespace TenantFlow.Tenants.Domain.Tenants;

/// <summary>
/// The Tenants module's error catalog (ADR-0016). Codes are a public contract: never rename or reuse one.
/// Messages are fixed sentences without user input.
/// </summary>
public static class TenantErrors
{
    // Shared with the other modules through the contract, so "no such tenant" reads the same everywhere.
    public static readonly AppError NotFound = TenantContractErrors.NotFound;

    public static readonly AppError AlreadySuspended =
        new("tenant.already_suspended", ErrorKind.Conflict, "The tenant is already suspended.");

    public static readonly AppError NotSuspended =
        new("tenant.not_suspended", ErrorKind.Conflict, "The tenant is not suspended, so it cannot be reactivated.");

    public static readonly AppError NameTaken =
        new("tenant.name_taken", ErrorKind.Conflict, "A tenant with this name already exists.");

    public static readonly AppError ConcurrentUpdate =
        new("tenant.concurrent_update", ErrorKind.Conflict, "The tenant was changed by another request. Retry with its current state.");

    public static readonly AppError NameRequired =
        new("tenant.name.required", ErrorKind.Validation, "Name is required.");

    public static readonly AppError NameInvalidCharacters =
        new("tenant.name.invalid_characters", ErrorKind.Validation, "Name must not contain control or text-direction override characters.");

    public static readonly AppError NameTooLong =
        new("tenant.name.too_long", ErrorKind.Validation, $"Name must be at most {Tenant.NameMaxLength} characters.");
}
