namespace TenantFlow.Tenants.Contracts;

/// <summary>Whether a tenant can be used. A contract type of its own, so consumers never see the module's status type.</summary>
public enum TenantAvailability
{
    Active = 1,
    Suspended = 2,
}
