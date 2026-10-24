using TenantFlow.IdentityAccess.Contracts;

namespace TenantFlow.IdentityAccess.Application.TenantAccess;

/// <summary>
/// The tenant context of background work (ADR-0010 worker boundary, ADR-0021 decision 5, ADR-0022 decisions 6 and 7):
/// the Worker enters it once per message or timer, from a tenant the message envelope or timer row names, in a DI scope
/// of its own. It acts for no membership and holds no permission, so module code that needs a user (an owner check, a
/// permission) still fails closed; only tenant-scoped storage and contracts work. Unentered, it fails closed like the
/// request context.
/// </summary>
public sealed class ServiceTenantContext : ITenantContext
{
    /// <summary>The role name a service context reports; no permission is attached to it.</summary>
    public const string ServiceRole = "service";

    private Guid? _tenantId;

    public bool IsSet => _tenantId is not null;

    public Guid TenantId => _tenantId ?? throw new TenantContextNotSetException();

    /// <summary>Background work acts for no membership.</summary>
    /// <exception cref="TenantContextNotSetException">Always: there is no membership behind a service context.</exception>
    public Guid MembershipId => throw new TenantContextNotSetException();

    public string Role => IsSet ? ServiceRole : throw new TenantContextNotSetException();

    public bool HasPermission(string permission) => IsSet ? false : throw new TenantContextNotSetException();

    /// <summary>Sets the tenant for this scope, once.</summary>
    /// <exception cref="InvalidOperationException">The scope already acts for a tenant: one scope is one message or timer.</exception>
    public void Enter(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A tenant id is required.", nameof(tenantId));
        }

        if (_tenantId is not null)
        {
            throw new InvalidOperationException("This scope already acts for a tenant; create a new scope per message or timer.");
        }

        _tenantId = tenantId;
    }
}
