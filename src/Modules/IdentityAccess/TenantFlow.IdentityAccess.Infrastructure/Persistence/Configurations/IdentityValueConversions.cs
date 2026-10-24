using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TenantFlow.IdentityAccess.Domain.Memberships;

namespace TenantFlow.IdentityAccess.Infrastructure.Persistence.Configurations;

/// <summary>
/// How the module's value types are stored: ids as <c>uuid</c>, and roles and statuses by their stable names, never as
/// enum numbers. The names and the strict parse belong to the domain: a stored value nobody recognises fails loudly.
/// </summary>
internal static class IdentityValueConversions
{
    public static readonly ValueConverter<TenantId, Guid> TenantIdValue = new(id => id.Value, value => new TenantId(value));

    public static readonly ValueConverter<MembershipId, Guid> MembershipIdValue = new(id => id.Value, value => new MembershipId(value));

    public static readonly ValueConverter<MembershipRole, string> RoleRequired =
        new(role => role.ToName(), name => MembershipRoleNames.Parse(name));

    public static readonly ValueConverter<MembershipRole?, string?> RoleOptional =
        new(role => role == null ? null : role.Value.ToName(), name => name == null ? null : MembershipRoleNames.Parse(name));

    public static readonly ValueConverter<MembershipStatus, string> StatusRequired =
        new(status => status.ToName(), name => MembershipStatusNames.Parse(name));
}
