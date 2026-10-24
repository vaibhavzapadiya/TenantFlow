using TenantFlow.BuildingBlocks.Abstractions.Errors;

namespace TenantFlow.IdentityAccess.Domain.Memberships;

/// <summary>
/// A tenant that has had an administrator always keeps at least one (SPEC-0002 criterion 20). The count is read
/// while the tenant's membership changes are serialized, so two administrators can never remove each other.
/// </summary>
public static class AdministratorRule
{
    /// <param name="target">The membership about to change.</param>
    /// <param name="roleAfter">Its role after the change, or <c>null</c> when it is being removed.</param>
    /// <param name="activeAdministrators">Active administrators in the tenant right now, including the target.</param>
    /// <exception cref="AppException"><see cref="MembershipErrors.LastAdministrator"/> when the change leaves none.</exception>
    public static void EnsureTenantKeepsAdministrator(Membership target, MembershipRole? roleAfter, int activeAdministrators)
    {
        ArgumentNullException.ThrowIfNull(target);
        var losesAdministrator = target is { IsActive: true, Role: MembershipRole.TenantAdmin } && roleAfter != MembershipRole.TenantAdmin;
        if (losesAdministrator && activeAdministrators <= 1)
        {
            throw new AppException(MembershipErrors.LastAdministrator);
        }
    }
}
