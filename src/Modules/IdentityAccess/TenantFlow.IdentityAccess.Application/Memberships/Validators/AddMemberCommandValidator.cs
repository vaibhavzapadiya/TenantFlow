using FluentValidation;
using TenantFlow.BuildingBlocks.Abstractions.Identity;
using TenantFlow.BuildingBlocks.Validation;
using TenantFlow.IdentityAccess.Application.Memberships.Commands;
using TenantFlow.IdentityAccess.Domain.Memberships;

namespace TenantFlow.IdentityAccess.Application.Memberships.Validators;

/// <summary>
/// Reports the domain's rules as field errors (ADR-0014, ADR-0016). The subject rule is
/// <see cref="Membership.CheckSubject"/> and the role names are <see cref="MembershipRoleNames"/>; the validator holds
/// no rule of its own. Messages describe the rule and never echo the input.
/// </summary>
public sealed class AddMemberCommandValidator : AbstractValidator<AddMemberCommand>
{
    public AddMemberCommandValidator()
    {
        RuleFor(command => command.Issuer).Custom((issuer, context) => DomainRuleValidation.Report(
            context, string.IsNullOrWhiteSpace(issuer) || issuer.Length > IdentityLimits.IssuerMaxLength ? MembershipErrors.IssuerInvalid : null));
        RuleFor(command => command.Subject).Custom((subject, context) => DomainRuleValidation.Report(context, Membership.CheckSubject(subject)));
        RuleFor(command => command.Role).Custom(RoleRule.Check);
    }
}
