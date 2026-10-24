using FluentValidation;
using TenantFlow.BuildingBlocks.Validation;
using TenantFlow.Payments.Application.Payments.Commands;
using TenantFlow.Payments.Domain.Payments;

namespace TenantFlow.Payments.Application.Payments.Validators;

/// <summary>Reports the payment's input rules as field errors (ADR-0014). It holds no rule of its own: each is a <c>Payment.Check</c> method.</summary>
public sealed class CreatePaymentCommandValidator : AbstractValidator<CreatePaymentCommand>
{
    public CreatePaymentCommandValidator()
    {
        RuleFor(c => c.IdempotencyKey).Custom((key, context) => DomainRuleValidation.Report(context, Payment.CheckIdempotencyKey(key)));
        RuleFor(c => c.PaymentMethod).Custom((method, context) => DomainRuleValidation.Report(context, Payment.CheckPaymentMethod(method)));
    }
}
