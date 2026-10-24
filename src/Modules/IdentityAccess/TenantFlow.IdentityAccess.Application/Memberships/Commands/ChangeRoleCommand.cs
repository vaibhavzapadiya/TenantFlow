using TenantFlow.IdentityAccess.Application.Memberships.Validators;

namespace TenantFlow.IdentityAccess.Application.Memberships.Commands;

/// <summary>Input of the change-role use case, validated by <see cref="ChangeRoleCommandValidator"/>.</summary>
public sealed record ChangeRoleCommand(Guid TenantId, Guid MembershipId, string? Role);
