namespace TenantFlow.BuildingBlocks.Abstractions.Identity;

/// <summary>
/// Who performed an action: the token issuer plus the subject (ADR-0009). Neither can be blank, and neither can be
/// larger than the columns that store it (<see cref="IdentityLimits"/>), so an actor that exists can always be audited.
/// Tokens are validated before one is made; a failure here is a programming error, not a user mistake, and an error
/// never prints the value that caused it.
/// </summary>
public sealed record ActorIdentity
{
    public ActorIdentity(string issuer, string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(issuer.Length, IdentityLimits.IssuerMaxLength, nameof(issuer));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(subject.Length, IdentityLimits.SubjectMaxLength, nameof(subject));
        Issuer = issuer;
        Subject = subject;
    }

    public string Issuer { get; }

    public string Subject { get; }
}
