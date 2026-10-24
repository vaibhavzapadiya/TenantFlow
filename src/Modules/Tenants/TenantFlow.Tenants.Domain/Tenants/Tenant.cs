using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Abstractions.Text;

namespace TenantFlow.Tenants.Domain.Tenants;

/// <summary>
/// A customer organisation on the platform. Only the platform boundary changes its lifecycle
/// (ADR-0010); tenant users never do. Status changes follow <see cref="TenantStatus"/> transitions only.
/// </summary>
public sealed class Tenant
{
    public const int NameMaxLength = 200;

    private Tenant(Guid id, string name, DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        Status = TenantStatus.Active;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    public TenantStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// The one definition of a valid tenant name (ADR-0014): required, at most <see cref="NameMaxLength"/>
    /// characters after trimming, and free of control and text-direction override characters. The validator
    /// reports this as a field error; <see cref="Create"/> enforces it, so no caller can bypass it.
    /// Returns the first broken rule, or null.
    /// </summary>
    public static AppError? CheckName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return TenantErrors.NameRequired;
        }

        var trimmed = name.Trim();
        if (trimmed.Length > NameMaxLength)
        {
            return TenantErrors.NameTooLong;
        }

        return TextRules.ContainsDisallowedCharacters(trimmed) ? TenantErrors.NameInvalidCharacters : null;
    }

    /// <summary>Creates an active tenant.</summary>
    /// <exception cref="AppException">The name is invalid; see <see cref="CheckName"/>.</exception>
    /// <exception cref="ArgumentException">The id is empty: a programming error, never a user mistake.</exception>
    public static Tenant Create(Guid id, string? name, DateTimeOffset now)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A tenant needs an id.", nameof(id));
        }

        return CheckName(name) is { } error ? throw new AppException(error) : new Tenant(id, name!.Trim(), now);
    }

    // Control characters (NUL, newlines, tabs) and the bidirectional overrides, embeddings and isolates that
    // can make one name read as another in an admin screen or an audit view. Zero-width joiners and
    // non-joiners are allowed: Persian, Indic scripts, and emoji sequences need them.

    /// <exception cref="AppException"><see cref="TenantErrors.AlreadySuspended"/> when the tenant is not active.</exception>
    public void Suspend(DateTimeOffset now)
    {
        if (Status != TenantStatus.Active)
        {
            throw new AppException(TenantErrors.AlreadySuspended);
        }

        Status = TenantStatus.Suspended;
        UpdatedAt = now;
    }

    /// <exception cref="AppException"><see cref="TenantErrors.NotSuspended"/> when the tenant is not suspended.</exception>
    public void Reactivate(DateTimeOffset now)
    {
        if (Status != TenantStatus.Suspended)
        {
            throw new AppException(TenantErrors.NotSuspended);
        }

        Status = TenantStatus.Active;
        UpdatedAt = now;
    }
}
