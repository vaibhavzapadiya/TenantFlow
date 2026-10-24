using TenantFlow.Subscriptions.Application.Plans.Commands;
using TenantFlow.Subscriptions.Application.Plans.Validators;

namespace TenantFlow.Subscriptions.Tests;

/// <summary>SPEC-0007 criterion 1: a plan's input rules are reported as field errors with their codes.</summary>
public sealed class CreatePlanCommandValidatorTests
{
    [Fact]
    public void A_valid_plan_passes_and_every_broken_rule_is_reported_by_its_code()
    {
        var validator = new CreatePlanCommandValidator();

        var valid = validator.Validate(new CreatePlanCommand("Pro", 9.99m, "EUR", "month", 14));
        var invalid = validator.Validate(new CreatePlanCommand(" ", 0m, "EURO", "week", 91));

        Assert.True(valid.IsValid);
        Assert.Equal(
            ["subscriptions.plan.amount_invalid", "subscriptions.plan.currency_invalid", "subscriptions.plan.interval_invalid", "subscriptions.plan.name_invalid", "subscriptions.plan.trial_days_invalid"],
            invalid.Errors.Select(static e => e.ErrorCode).Order(StringComparer.Ordinal));
    }
}
