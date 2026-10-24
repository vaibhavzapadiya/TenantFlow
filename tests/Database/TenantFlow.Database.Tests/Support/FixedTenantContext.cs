using TenantFlow.IdentityAccess.Contracts;

namespace TenantFlow.Database.Tests.Support;

/// <summary>
/// A tenant context set to one tenant, for tests that call a module contract directly instead of through a route (which
/// resolves the context from a membership). It grants no permission: contracts do not check permissions.
/// </summary>
internal sealed class FixedTenantContext(Guid tenantId) : ITenantContext
{
    public bool IsSet => true;

    public Guid TenantId => tenantId;

    public Guid MembershipId { get; } = Guid.CreateVersion7();

    public string Role => "tenant-admin";

    public bool HasPermission(string permission) => false;
}
