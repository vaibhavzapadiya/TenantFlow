namespace TenantFlow.Audit.Contracts;

/// <summary>Who can act, as a closed list (ADR-0026 decision 3). Each kind has its own kind of reference.</summary>
public static class AuditActorKinds
{
    /// <summary>A tenant member; the reference is the acting membership id.</summary>
    public const string Member = "member";

    /// <summary>A platform administrator; the reference is a keyed hash of the operator's issuer and subject.</summary>
    public const string PlatformAdmin = "platform_admin";

    /// <summary>A support operator (SPEC-0009 S7); the reference is a keyed hash, like a platform administrator's.</summary>
    public const string SupportOperator = "support_operator";

    /// <summary>A background worker acting on its own; the reference names the service.</summary>
    public const string Service = "service";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Member, PlatformAdmin, SupportOperator, Service };
}
