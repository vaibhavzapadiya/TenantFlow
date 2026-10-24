using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Abstractions.Identity;

namespace TenantFlow.IdentityAccess.Domain.Memberships;

/// <summary>
/// One user's place in one tenant: issuer plus subject (ADR-0009), a role, and a status. It is the only source of a
/// user's authority inside the tenant (ADR-0010). A removed membership never comes back; adding the user again makes a
/// new membership.
/// </summary>
public sealed class Membership
{
    // Parameter names match the properties, because this is also the constructor the database mapping uses.
    private Membership(MembershipId id, TenantId tenantId, string issuer, string subject, MembershipRole role, DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        Issuer = issuer;
        Subject = subject;
        Role = role;
        Status = MembershipStatus.Active;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public MembershipId Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public string Issuer { get; private set; }

    public string Subject { get; private set; }

    public MembershipRole Role { get; private set; }

    public MembershipStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsActive => Status == MembershipStatus.Active;

    /// <summary>
    /// The one definition of a valid member subject: present, at most <see cref="IdentityLimits.SubjectMaxLength"/>
    /// characters, and free of control characters (they would forge lines in logs and audit views). It is not trimmed:
    /// a subject is an identifier and must match the token's exactly. Returns the first broken rule, or null.
    /// </summary>
    public static AppError? CheckSubject(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return MembershipErrors.SubjectRequired;
        }

        if (subject.Length > IdentityLimits.SubjectMaxLength)
        {
            return MembershipErrors.SubjectTooLong;
        }

        return subject.Any(char.IsControl) ? MembershipErrors.SubjectInvalidCharacters : null;
    }

    /// <summary>Creates an active membership.</summary>
    /// <exception cref="ArgumentException">An id is empty or the role is undefined: programming errors, never user mistakes.</exception>
    public static Membership Create(MembershipId id, TenantId tenantId, ActorIdentity member, MembershipRole role, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("A membership needs an id.", nameof(id));
        }

        if (tenantId.Value == Guid.Empty)
        {
            throw new ArgumentException("A membership needs a tenant.", nameof(tenantId));
        }

        EnsureDefined(role);
        return new Membership(id, tenantId, member.Issuer, member.Subject, role, now);
    }

    /// <summary>Gives the member a new role and returns the one they held.</summary>
    /// <exception cref="AppException"><see cref="MembershipErrors.NotFound"/> when the membership was removed.</exception>
    public MembershipRole ChangeRole(MembershipRole newRole, DateTimeOffset now)
    {
        EnsureActive();
        EnsureDefined(newRole);
        var old = Role;
        Role = newRole;
        UpdatedAt = now;
        return old;
    }

    /// <exception cref="AppException"><see cref="MembershipErrors.NotFound"/> when the membership was already removed.</exception>
    public void Remove(DateTimeOffset now)
    {
        EnsureActive();
        Status = MembershipStatus.Removed;
        UpdatedAt = now;
    }

    private static void EnsureDefined(MembershipRole role)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), "The membership role is not one the domain defines.");
        }
    }

    private void EnsureActive()
    {
        if (!IsActive)
        {
            throw new AppException(MembershipErrors.NotFound);
        }
    }
}
