namespace TenantFlow.IdentityAccess.Contracts;

/// <summary>
/// Tenant-owned code ran with no tenant context. A programming error, never a user mistake: the code must not run
/// unscoped, so it stops here instead of falling back to "all tenants" or a default tenant (SPEC-0002 criterion 15).
/// </summary>
public sealed class TenantContextNotSetException : InvalidOperationException
{
    public TenantContextNotSetException()
        : base("No tenant context is set for this request, so tenant-owned code cannot run.")
    {
    }

    public TenantContextNotSetException(string message)
        : base(message)
    {
    }

    public TenantContextNotSetException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
