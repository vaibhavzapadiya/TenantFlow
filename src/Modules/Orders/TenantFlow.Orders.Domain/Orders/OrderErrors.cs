using TenantFlow.BuildingBlocks.Abstractions.Errors;

namespace TenantFlow.Orders.Domain.Orders;

/// <summary>
/// The Orders module's error catalog (ADR-0016). Codes are a public contract: never rename or reuse one.
/// Messages are fixed sentences without user input.
/// </summary>
public static class OrderErrors
{
    public static readonly AppError CancellationReasonInvalid =
        new("orders.cancellation_reason_invalid", ErrorKind.Validation, "Cancelling a paid order needs a reason of 1 to 500 characters without control characters.");

    public static readonly AppError RefundPermissionRequired =
        new("orders.refund_permission_required", ErrorKind.Forbidden, "Only an order manager can cancel a paid order, because it refunds the payment.");

    public static readonly AppError NotFound =
        new("orders.order_not_found", ErrorKind.NotFound, "The order does not exist.");

    public static readonly AppError InvalidTransition =
        new("orders.invalid_transition", ErrorKind.Conflict, "The order's current state does not allow this action.");

    public static readonly AppError PaymentInProgress =
        new("orders.payment_in_progress", ErrorKind.Conflict, "A payment for the order has begun, so it can no longer be cancelled.");

    public static readonly AppError VersionConflict =
        new("orders.version_conflict", ErrorKind.PreconditionFailed, "The order was changed by another request. Fetch its current state and retry.");

    public static readonly AppError IfMatchRequired =
        new("orders.if_match_required", ErrorKind.Validation, "An If-Match header with the order's current ETag is required.");

    public static readonly AppError ProductUnavailable =
        new("orders.product_unavailable", ErrorKind.Conflict, "A product in the order is not available for ordering.");

    public static readonly AppError MixedCurrency =
        new("orders.mixed_currency", ErrorKind.Conflict, "All products in an order must have the same currency.");

    public static readonly AppError TotalTooLarge =
        new("orders.total_too_large", ErrorKind.Conflict, "The order total is larger than an order can hold.");

    public static readonly AppError LinesRequired =
        new("orders.lines.required", ErrorKind.Validation, "An order needs at least one line.");

    public static readonly AppError LinesTooMany =
        new("orders.lines.too_many", ErrorKind.Validation, "An order can have at most 50 lines.");

    public static readonly AppError ProductDuplicated =
        new("orders.lines.product_duplicated", ErrorKind.Validation, "Each product may appear in only one line.");

    public static readonly AppError ProductIdRequired =
        new("orders.line.product_id_required", ErrorKind.Validation, "Product id is required.");

    public static readonly AppError QuantityInvalid =
        new("orders.line.quantity_invalid", ErrorKind.Validation, "Quantity is required and must be between 1 and 10000.");
}
