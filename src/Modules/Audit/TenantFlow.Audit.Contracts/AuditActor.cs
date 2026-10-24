namespace TenantFlow.Audit.Contracts;

/// <summary>
/// Who performed an audited action, without personal data (ADR-0026 decision 3): a member by their membership id, an
/// operator by a keyed hash of issuer and subject that the API host computes. No OIDC subject, name or email can be put
/// in one, because each kind only accepts its own shape of reference.
/// </summary>
public sealed record AuditActor
{
    /// <summary>The length of an operator reference: a SHA-256 HMAC as lowercase hex.</summary>
    public const int OperatorReferenceLength = 64;

    private AuditActor(string kind, string reference)
    {
        Kind = kind;
        Reference = reference;
    }

    /// <summary>One of <see cref="AuditActorKinds"/>.</summary>
    public string Kind { get; }

    public string Reference { get; }

    /// <exception cref="ArgumentException">An empty membership id: a programming error.</exception>
    public static AuditActor Member(Guid membershipId) =>
        membershipId == Guid.Empty
            ? throw new ArgumentException("A member actor needs a membership id.", nameof(membershipId))
            : new AuditActor(AuditActorKinds.Member, membershipId.ToString("D"));

    /// <exception cref="ArgumentException">Not a 64-character lowercase hex reference: a programming error.</exception>
    public static AuditActor PlatformAdmin(string operatorReference) =>
        new(AuditActorKinds.PlatformAdmin, CheckOperatorReference(operatorReference));

    /// <exception cref="ArgumentException">Not a 64-character lowercase hex reference: a programming error.</exception>
    public static AuditActor SupportOperator(string operatorReference) =>
        new(AuditActorKinds.SupportOperator, CheckOperatorReference(operatorReference));

    /// <summary>True when <paramref name="reference"/> is a valid reference for an actor of <paramref name="kind"/>.</summary>
    public static bool IsValid(string? kind, string? reference) => kind switch
    {
        AuditActorKinds.Member => Guid.TryParseExact(reference, "D", out var id) && id != Guid.Empty,
        AuditActorKinds.PlatformAdmin or AuditActorKinds.SupportOperator => IsOperatorReference(reference),
        AuditActorKinds.Service => !string.IsNullOrEmpty(reference) && reference.Length <= OperatorReferenceLength && reference.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-' or '_'),
        _ => false,
    };

    private static string CheckOperatorReference(string reference) =>
        IsOperatorReference(reference) ? reference : throw new ArgumentException("An operator reference is 64 lowercase hex characters.", nameof(reference));

    private static bool IsOperatorReference(string? reference) =>
        reference is { Length: OperatorReferenceLength } && reference.All(static c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
}
