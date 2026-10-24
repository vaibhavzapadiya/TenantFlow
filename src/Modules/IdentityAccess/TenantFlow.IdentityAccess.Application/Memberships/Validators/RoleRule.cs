using FluentValidation;
using TenantFlow.BuildingBlocks.Validation;
using TenantFlow.IdentityAccess.Domain.Memberships;

namespace TenantFlow.IdentityAccess.Application.Memberships.Validators;

/// <summary>The role-name check both commands share, so there is one message and one code for it.</summary>
internal static class RoleRule
{
    public static void Check<T>(string? role, ValidationContext<T> context) =>
        DomainRuleValidation.Report(context, MembershipRoleNames.TryParse(role, out _) ? null : MembershipErrors.RoleInvalid);
}
