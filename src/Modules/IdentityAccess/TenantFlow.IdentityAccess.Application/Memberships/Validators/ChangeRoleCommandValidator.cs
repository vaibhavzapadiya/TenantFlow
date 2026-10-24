using FluentValidation;
using TenantFlow.IdentityAccess.Application.Memberships.Commands;

namespace TenantFlow.IdentityAccess.Application.Memberships.Validators;

/// <summary>The role must be one of the domain's role names (ADR-0014).</summary>
public sealed class ChangeRoleCommandValidator : AbstractValidator<ChangeRoleCommand>
{
    public ChangeRoleCommandValidator()
    {
        RuleFor(command => command.Role).Custom(RoleRule.Check);
    }
}
