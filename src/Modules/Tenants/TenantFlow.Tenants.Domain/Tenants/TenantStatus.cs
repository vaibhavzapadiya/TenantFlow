namespace TenantFlow.Tenants.Domain.Tenants;

/// <summary>Lifecycle status of a tenant (SPEC-0002 criterion 25).</summary>
public enum TenantStatus
{
    Active = 1,
    Suspended = 2,
}
