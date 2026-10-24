using TenantFlow.BuildingBlocks.Abstractions.Errors;

namespace TenantFlow.Tenants.Contracts;

/// <summary>
/// The errors about a tenant that other modules answer with (ADR-0016). They are defined once, here, so a tenant
/// that does not exist or is suspended has one code and one message whichever module notices it. The Tenants
/// module's own catalog reuses <see cref="NotFound"/>. Codes are a public contract: never rename or reuse one.
/// </summary>
public static class TenantContractErrors
{
    public static readonly AppError NotFound =
        new("tenant.not_found", ErrorKind.NotFound, "The tenant does not exist.");

    public static readonly AppError Suspended =
        new("tenant.suspended", ErrorKind.Conflict, "The tenant is suspended, so it accepts no membership changes.");
}
