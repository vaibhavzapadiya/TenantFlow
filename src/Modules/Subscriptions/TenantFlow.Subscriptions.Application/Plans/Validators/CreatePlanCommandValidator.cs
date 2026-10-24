using FluentValidation;
using TenantFlow.BuildingBlocks.Validation;
using TenantFlow.Subscriptions.Application.Plans.Commands;
using TenantFlow.Subscriptions.Domain.Plans;

namespace TenantFlow.Subscriptions.Application.Plans.Validators;

/// <summary>Reports the plan's input rules as field errors (ADR-0014). It holds no rule of its own: each is a <c>Plan.Check</c> method.</summary>
public sealed class CreatePlanCommandValidator : AbstractValidator<CreatePlanCommand>
{
    public CreatePlanCommandValidator()
    {
        RuleFor(c => c.Name).Custom((value, context) => DomainRuleValidation.Report(context, Plan.CheckName(value)));
        RuleFor(c => c.Amount).Custom((value, context) => DomainRuleValidation.Report(context, Plan.CheckAmount(value)));
        RuleFor(c => c.Currency).Custom((value, context) => DomainRuleValidation.Report(context, Plan.CheckCurrency(value)));
        RuleFor(c => c.Interval).Custom((value, context) => DomainRuleValidation.Report(context, Plan.CheckInterval(value)));
        RuleFor(c => c.TrialDays).Custom((value, context) => DomainRuleValidation.Report(context, Plan.CheckTrialDays(value)));
    }
}
