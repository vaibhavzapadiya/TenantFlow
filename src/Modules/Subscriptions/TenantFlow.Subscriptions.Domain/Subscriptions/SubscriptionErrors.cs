using TenantFlow.BuildingBlocks.Abstractions.Errors;

namespace TenantFlow.Subscriptions.Domain.Subscriptions;

/// <summary>
/// The Subscriptions module's error catalog (ADR-0016). Codes are a public contract: never rename or reuse one.
/// Messages are fixed sentences without user input; a rejected payment method is never echoed.
/// </summary>
public static class SubscriptionErrors
{
    public static readonly AppError PlanNotFound =
        new("subscriptions.plan_not_found", ErrorKind.NotFound, "The plan does not exist.");

    public static readonly AppError NotFound =
        new("subscriptions.subscription_not_found", ErrorKind.NotFound, "The subscription does not exist.");

    public static readonly AppError AlreadySubscribed =
        new("subscriptions.already_subscribed", ErrorKind.Conflict, "You already have a live subscription to this plan.");

    public static readonly AppError InvalidTransition =
        new("subscriptions.invalid_transition", ErrorKind.Conflict, "The subscription cannot do that in its current state.");

    public static readonly AppError PlanNameInvalid =
        new("subscriptions.plan.name_invalid", ErrorKind.Validation, "Name must be 1 to 100 characters without control characters.");

    public static readonly AppError PlanAmountInvalid =
        new("subscriptions.plan.amount_invalid", ErrorKind.Validation, "Amount must be greater than zero with at most 4 decimal places.");

    public static readonly AppError PlanCurrencyInvalid =
        new("subscriptions.plan.currency_invalid", ErrorKind.Validation, "Currency must be a three-letter ISO 4217 code.");

    public static readonly AppError PlanIntervalInvalid =
        new("subscriptions.plan.interval_invalid", ErrorKind.Validation, "Interval must be 'month' or 'year'.");

    public static readonly AppError PlanTrialDaysInvalid =
        new("subscriptions.plan.trial_days_invalid", ErrorKind.Validation, "Trial days must be between 0 and 90.");

    public static readonly AppError PaymentMethodInvalid =
        new("subscriptions.payment_method.invalid", ErrorKind.Validation, "Payment method must be a provider token of 3 to 100 letters, digits or underscores. Card numbers are not accepted.");
}
