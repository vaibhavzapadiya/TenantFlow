using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Abstractions.Identity;

namespace TenantFlow.IdentityAccess.Domain.Memberships;

/// <summary>
/// The IdentityAccess module's error catalog (ADR-0016). Codes are a public contract: never rename or reuse one.
/// Messages are fixed sentences without user input.
/// </summary>
public static class MembershipErrors
{
    public static readonly AppError NotFound =
        new("membership.not_found", ErrorKind.NotFound, "The membership does not exist.");

    public static readonly AppError AlreadyExists =
        new("membership.already_exists", ErrorKind.Conflict, "The user is already a member of this tenant.");

    public static readonly AppError LastAdministrator =
        new("membership.last_administrator", ErrorKind.Conflict, "The tenant must keep at least one administrator.");

    public static readonly AppError PermissionDenied =
        new("membership.permission_denied", ErrorKind.Forbidden, "Your role does not allow this action.");

    public static readonly AppError TenantSuspended =
        new("membership.tenant_suspended", ErrorKind.Forbidden, "The tenant is suspended, so its members cannot use it.");

    public static readonly AppError RoleInvalid =
        new("membership.role.invalid", ErrorKind.Validation, "Role must be tenant-admin or customer.");

    public static readonly AppError SubjectRequired =
        new("membership.subject.required", ErrorKind.Validation, "Subject is required.");

    public static readonly AppError SubjectTooLong =
        new("membership.subject.too_long", ErrorKind.Validation, $"Subject must be at most {IdentityLimits.SubjectMaxLength} characters.");

    public static readonly AppError SubjectInvalidCharacters =
        new("membership.subject.invalid_characters", ErrorKind.Validation, "Subject must not contain control characters.");

    public static readonly AppError IssuerInvalid =
        new("membership.issuer.invalid", ErrorKind.Validation, "Issuer is required and must fit the stored limit.");
}
