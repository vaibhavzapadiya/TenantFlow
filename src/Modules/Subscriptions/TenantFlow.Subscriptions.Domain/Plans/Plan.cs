using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Abstractions.Text;
using TenantFlow.Subscriptions.Domain.Subscriptions;

namespace TenantFlow.Subscriptions.Domain.Plans;

/// <summary>
/// A plan a tenant sells (SPEC-0007 criterion 1): a price per interval, as a decimal amount plus an ISO currency
/// (AGENTS.md rule 4), and an optional free trial. A subscription copies the price when it starts, so changing plans
/// later never changes what an existing subscriber pays.
/// </summary>
public sealed class Plan
{
    public const int MaxNameLength = 100;
    public const int MaxTrialDays = 90;
    public const int AmountScale = 4;

    private Plan()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public decimal Amount { get; private set; }

    /// <summary>ISO 4217 code of <see cref="Amount"/>, upper case.</summary>
    public string Currency { get; private set; } = string.Empty;

    public BillingInterval Interval { get; private set; }

    public int TrialDays { get; private set; }

    public bool Active { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static AppError? CheckName(string? name) =>
        name?.Trim() is { Length: > 0 and <= MaxNameLength } trimmed && !TextRules.ContainsDisallowedCharacters(trimmed) ? null : SubscriptionErrors.PlanNameInvalid;

    public static AppError? CheckAmount(decimal? amount) =>
        amount is { } value && value > 0 && value.Scale <= AmountScale && value < 1_000_000_000_000m ? null : SubscriptionErrors.PlanAmountInvalid;

    public static AppError? CheckCurrency(string? currency) =>
        currency is { Length: 3 } && currency.All(static c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') ? null : SubscriptionErrors.PlanCurrencyInvalid;

    public static AppError? CheckInterval(string? interval) =>
        BillingIntervalNames.TryParse(interval, out _) ? null : SubscriptionErrors.PlanIntervalInvalid;

    public static AppError? CheckTrialDays(int? trialDays) =>
        trialDays is >= 0 and <= MaxTrialDays ? null : SubscriptionErrors.PlanTrialDaysInvalid;

    /// <summary>Creates an active plan.</summary>
    /// <exception cref="AppException">An input breaks a rule; see the <c>Check</c> methods.</exception>
    /// <exception cref="ArgumentException">An id is empty: a programming error.</exception>
    public static Plan Create(Guid id, Guid tenantId, string? name, decimal? amount, string? currency, string? interval, int? trialDays, DateTimeOffset now)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty)
        {
            throw new ArgumentException("A plan needs an id and a tenant id.");
        }

        if ((CheckName(name) ?? CheckAmount(amount) ?? CheckCurrency(currency) ?? CheckInterval(interval) ?? CheckTrialDays(trialDays)) is { } error)
        {
            throw new AppException(error);
        }

        return new Plan
        {
            Id = id,
            TenantId = tenantId,
            Name = name!.Trim(),
            Amount = amount!.Value,
            Currency = currency!.ToUpperInvariant(),
            Interval = BillingIntervalNames.Parse(interval!),
            TrialDays = trialDays!.Value,
            Active = true,
            CreatedAt = now,
        };
    }
}
