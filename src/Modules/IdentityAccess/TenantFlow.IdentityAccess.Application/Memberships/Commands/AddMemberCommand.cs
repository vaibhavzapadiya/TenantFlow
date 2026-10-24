using TenantFlow.IdentityAccess.Application.Memberships.Validators;

namespace TenantFlow.IdentityAccess.Application.Memberships.Commands;

/// <summary>
/// Input of the add-member use case, validated by <see cref="AddMemberCommandValidator"/>. The issuer is the identity
/// provider the member signs in with (ADR-0009); the caller supplies it, so a subject from one provider is never
/// matched against another's.
/// </summary>
public sealed record AddMemberCommand(Guid TenantId, string? Issuer, string? Subject, string? Role);
