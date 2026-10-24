using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.Catalog.Contracts;

namespace TenantFlow.Catalog.Domain.Products;

/// <summary>
/// The Catalog module's error catalog (ADR-0016). Codes are a public contract: never rename or reuse one.
/// Messages are fixed sentences without user input.
/// </summary>
public static class ProductErrors
{
    // Shared with the other modules through the contract, so "no such product" reads the same everywhere.
    public static readonly AppError NotFound = CatalogContractErrors.ProductNotFound;

    public static readonly AppError SkuTaken =
        new("catalog.sku_taken", ErrorKind.Conflict, "A product with this SKU already exists.");

    public static readonly AppError VersionConflict =
        new("catalog.version_conflict", ErrorKind.PreconditionFailed, "The product was changed by another request. Fetch its current state and retry.");

    public static readonly AppError IfMatchRequired =
        new("catalog.if_match_required", ErrorKind.Validation, "An If-Match header with the product's current ETag is required.");

    public static readonly AppError CursorInvalid =
        new("catalog.cursor_invalid", ErrorKind.Validation, "The cursor is not valid.");

    public static readonly AppError SearchInvalid =
        new("catalog.search_invalid", ErrorKind.Validation, "The search text must be at most 100 characters.");

    public static readonly AppError LimitInvalid =
        new("catalog.limit_invalid", ErrorKind.Validation, "The limit must be between 1 and 100.");

    public static readonly AppError SkuRequired =
        new("catalog.sku.required", ErrorKind.Validation, "SKU is required.");

    public static readonly AppError SkuTooLong =
        new("catalog.sku.too_long", ErrorKind.Validation, "SKU is too long.");

    public static readonly AppError SkuInvalidCharacters =
        new("catalog.sku.invalid_characters", ErrorKind.Validation, "SKU must not contain control or text-direction override characters.");

    public static readonly AppError NameRequired =
        new("catalog.name.required", ErrorKind.Validation, "Name is required.");

    public static readonly AppError NameTooLong =
        new("catalog.name.too_long", ErrorKind.Validation, "Name is too long.");

    public static readonly AppError NameInvalidCharacters =
        new("catalog.name.invalid_characters", ErrorKind.Validation, "Name must not contain control or text-direction override characters.");

    public static readonly AppError PriceAmountInvalid =
        new("catalog.price.amount_invalid", ErrorKind.Validation, "Price amount is required, must not be negative or larger than 999999999999999.9999, and may have at most 4 decimal places.");

    public static readonly AppError PriceCurrencyInvalid =
        new("catalog.price.currency_invalid", ErrorKind.Validation, "Price currency must be a three-letter ISO 4217 code.");

    public static readonly AppError StatusInvalid =
        new("catalog.status.invalid", ErrorKind.Validation, "Status must be active or archived.");
}
