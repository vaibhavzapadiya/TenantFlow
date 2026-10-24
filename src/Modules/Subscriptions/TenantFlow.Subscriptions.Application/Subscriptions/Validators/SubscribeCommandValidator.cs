using FluentValidation;
using TenantFlow.BuildingBlocks.Validation;
using TenantFlow.Subscriptions.Application.Subscriptions.Commands;
using TenantFlow.Subscriptions.Domain.Subscriptions;

namespace TenantFlow.Subscriptions.Application.Subscriptions.Validators;

/// <summary>Reports the subscribe request's input rules as field errors (ADR-0014).</summary>
public sealed class SubscribeCommandValidator : AbstractValidator<SubscribeCommand>
{
    public SubscribeCommandValidator()
    {
        RuleFor(c => c.PaymentMethod).Custom((value, context) => DomainRuleValidation.Report(context, Subscription.CheckPaymentMethod(value)));
    }
}
