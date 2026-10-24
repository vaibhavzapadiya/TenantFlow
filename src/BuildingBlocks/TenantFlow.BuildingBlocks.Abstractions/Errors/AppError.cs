namespace TenantFlow.BuildingBlocks.Abstractions.Errors;

/// <summary>
/// A stable, named failure that clients can rely on. <see cref="Code"/> is the contract: lowercase
/// <c>area.reason</c> segments such as <c>tenant.not_found</c>. It never changes once released, and
/// clients branch on it, never on <see cref="Message"/>. The message is a fixed human-readable sentence
/// that never contains user input, identifiers of other tenants, or internals (ADR-0016).
/// </summary>
/// <param name="Code">Stable machine-readable code, for example <c>tenant.already_suspended</c>.</param>
/// <param name="Kind">The transport-agnostic category; the API turns it into a status code.</param>
/// <param name="Message">Fixed, actionable, safe-to-show text.</param>
public sealed record AppError(string Code, ErrorKind Kind, string Message)
{
    /// <summary>The naming convention every code must follow: lowercase snake_case segments joined by dots.</summary>
    public const string CodePattern = @"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$";
}
