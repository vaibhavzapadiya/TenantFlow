namespace TenantFlow.IdentityAccess.Domain.Memberships;

/// <summary>The id of one membership. Distinct from <see cref="TenantId"/> on purpose; see there.</summary>
public readonly record struct MembershipId
{
    public MembershipId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A membership id cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}
