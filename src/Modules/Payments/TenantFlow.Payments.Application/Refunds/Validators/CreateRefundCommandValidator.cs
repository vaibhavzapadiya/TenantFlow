using FluentValidation;
using TenantFlow.BuildingBlocks.Validation;
using TenantFlow.Payments.Application.Refunds.Commands;
using TenantFlow.Payments.Domain.Payments;
using TenantFlow.Payments.Domain.Refunds;

namespace TenantFlow.Payments.Application.Refunds.Validators;

/// <summary>Reports the refund's input rules as field errors (ADR-0014). Each rule is a <c>Check</c> method of the domain.</summary>
public sealed class CreateRefundCommandValidator : AbstractValidator<CreateRefundCommand>
{
    public CreateRefundCommandValidator()
    {
        RuleFor(c => c.IdempotencyKey).Custom((key, context) => DomainRuleValidation.Report(context, Payment.CheckIdempotencyKey(key)));
        RuleFor(c => c.Amount).Custom((amount, context) => DomainRuleValidation.Report(context, Refund.CheckAmount(amount)));
        RuleFor(c => c.Reason).Custom((reason, context) => DomainRuleValidation.Report(context, Refund.CheckReason(reason)));
    }
}
