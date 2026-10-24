using FluentValidation;
using TenantFlow.BuildingBlocks.Validation;
using TenantFlow.Tenants.Application.Lifecycle.Commands;
using TenantFlow.Tenants.Domain.Tenants;

namespace TenantFlow.Tenants.Application.Lifecycle.Validators;

/// <summary>
/// Reports the domain's rules as field errors (ADR-0014, ADR-0016). It holds no rule of its own: the name
/// rule is <see cref="Tenant.CheckName"/>, so the API and every other caller of <c>Tenant.Create</c> can
/// never disagree. Messages describe the rule and never echo the input.
/// </summary>
public sealed class CreateTenantCommandValidator : AbstractValidator<CreateTenantCommand>
{
    public CreateTenantCommandValidator()
    {
        RuleFor(command => command.Name).Custom((name, context) => DomainRuleValidation.Report(context, Tenant.CheckName(name)));
    }
}
