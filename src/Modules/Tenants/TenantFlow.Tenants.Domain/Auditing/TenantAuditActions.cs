namespace TenantFlow.Tenants.Domain.Auditing;

/// <summary>The audit vocabulary of the Tenants module. Stored as text, so these values never change.</summary>
public static class TenantAuditActions
{
    public const string Created = "tenant.created";
    public const string Suspended = "tenant.suspended";
    public const string Reactivated = "tenant.reactivated";
}
