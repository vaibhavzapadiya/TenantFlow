using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.ImportsExports.Domain.Jobs;
using TenantFlow.Tenants.Contracts;

namespace TenantFlow.ImportsExports.Application.Files;

/// <summary>
/// Whether a job may still act for the member who submitted it (SPEC-0009 criterion 11, M8 review item L10): the tenant
/// must still be active, and the requester still an active member holding <c>catalog.manage</c>. Runners ask before each
/// step that writes, in the job's tenant context, so a suspension or a removal stops the job at its next step.
/// </summary>
public sealed class RequesterCheck(ITenantContext context, ITenantDirectory tenants, IMembershipDirectory members)
{
    /// <summary>Null when the job may go on; otherwise the failure code that ends it.</summary>
    public async Task<string?> RefusalAsync(Guid requestedBy, CancellationToken cancellationToken)
    {
        var tenantId = context.TenantId;
        var availability = await tenants.GetAvailabilityAsync([tenantId], cancellationToken);
        if (!availability.TryGetValue(tenantId, out var status) || status != TenantAvailability.Active)
        {
            return JobFailureCodes.TenantSuspended;
        }

        return await members.HasPermissionAsync(requestedBy, TenantPermissions.CatalogManage, cancellationToken)
            ? null
            : JobFailureCodes.RequesterNotAuthorized;
    }
}
