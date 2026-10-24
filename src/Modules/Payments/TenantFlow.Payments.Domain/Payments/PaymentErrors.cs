using TenantFlow.BuildingBlocks.Abstractions.Errors;

namespace TenantFlow.Payments.Domain.Payments;

/// <summary>
/// The Payments module's error catalog (ADR-0016). Codes are a public contract: never rename or reuse one.
/// Messages are fixed sentences without user input; a rejected payment method is never echoed.
/// </summary>
public static class PaymentErrors
{
    public static readonly AppError NotFound =
        new("payments.payment_not_found", ErrorKind.NotFound, "The order has no payment.");

    public static readonly AppError AlreadyExists =
        new("payments.already_exists", ErrorKind.Conflict, "The order already has a payment.");

    public static readonly AppError NothingToPay =
        new("payments.nothing_to_pay", ErrorKind.Conflict, "The order's total is zero, so there is nothing to pay.");

    public static readonly AppError RequestInProgress =
        new("payments.request_in_progress", ErrorKind.Conflict, "A request with this Idempotency-Key is still being processed. Retry later.");

    public static readonly AppError IdempotencyKeyReused =
        new("payments.idempotency_key_reused", ErrorKind.UnprocessableContent, "This Idempotency-Key was already used with a different request.");

    public static readonly AppError WebhookProviderUnknown =
        new("payments.webhook.provider_unknown", ErrorKind.NotFound, "No payment provider of that name sends webhooks here.");

    public static readonly AppError WebhookSignatureInvalid =
        new("payments.webhook.signature_invalid", ErrorKind.Unauthenticated, "The webhook signature does not verify.");

    public static readonly AppError WebhookTimestampOutOfTolerance =
        new("payments.webhook.timestamp_out_of_tolerance", ErrorKind.Unauthenticated, "The webhook was signed too long ago or in the future.");

    public static readonly AppError WebhookMalformed =
        new("payments.webhook.malformed", ErrorKind.Validation, "The webhook body is not an event that can be read.");

    public static readonly AppError WebhookPaymentNotFound =
        new("payments.webhook.payment_not_found", ErrorKind.NotFound, "The webhook names no known payment.");

    public static readonly AppError IdempotencyKeyInvalid =
        new("payments.idempotency_key.invalid", ErrorKind.Validation, "An Idempotency-Key header of 1 to 255 visible ASCII characters is required.");

    public static readonly AppError NotRefundable =
        new("payments.not_refundable", ErrorKind.Conflict, "Only a succeeded payment can be refunded.");

    public static readonly AppError RefundExceedsCaptured =
        new("payments.refund_exceeds_captured", ErrorKind.Conflict, "The refunds would exceed the amount the payment captured.");

    public static readonly AppError RefundAmountInvalid =
        new("payments.refund.amount_invalid", ErrorKind.Validation, "Amount must be greater than zero with at most 4 decimal places.");

    public static readonly AppError RefundReasonInvalid =
        new("payments.refund.reason_invalid", ErrorKind.Validation, "Reason must be 1 to 500 characters without control characters.");

    public static readonly AppError PaymentMethodInvalid =
        new("payments.payment_method.invalid", ErrorKind.Validation, "Payment method must be a provider token of 3 to 100 letters, digits or underscores. Card numbers are not accepted.");
}
