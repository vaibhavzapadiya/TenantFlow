namespace TenantFlow.IdentityAccess.Domain.Memberships;

/// <summary>
/// A tenant's id as IdentityAccess sees it. A distinct type, so a membership id can never be passed where a tenant id
/// is expected (SPEC-0002 criterion 17 is exactly that bug class). It is made from the tenant's <see cref="Guid"/> at the
/// module boundary; the value is never empty. <c>default(TenantId)</c> is the one way to get an empty one and every
/// factory that takes a <see cref="TenantId"/> refuses it.
/// </summary>
public readonly record struct TenantId
{
    public TenantId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A tenant id cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}
