using TenantFlow.BuildingBlocks.Abstractions.Errors;

namespace TenantFlow.Inventory.Domain.Stock;

/// <summary>
/// The Inventory module's error catalog (ADR-0016). Codes are a public contract: never rename or reuse one.
/// Messages are fixed sentences without user input.
/// </summary>
public static class InventoryErrors
{
    public static readonly AppError InsufficientStock =
        new("inventory.insufficient_stock", ErrorKind.Conflict, "There is not enough stock for this adjustment.");

    public static readonly AppError DeltaInvalid =
        new("inventory.delta.invalid", ErrorKind.Validation, "Delta is required, must not be zero, and must be between -1000000 and 1000000.");

    public static readonly AppError ReasonRequired =
        new("inventory.reason.required", ErrorKind.Validation, "Reason is required.");

    public static readonly AppError ReasonTooLong =
        new("inventory.reason.too_long", ErrorKind.Validation, "Reason is too long.");

    public static readonly AppError ReasonInvalidCharacters =
        new("inventory.reason.invalid_characters", ErrorKind.Validation, "Reason must not contain control or text-direction override characters.");
}
