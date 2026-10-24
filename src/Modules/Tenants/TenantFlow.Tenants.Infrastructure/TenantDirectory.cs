using Microsoft.EntityFrameworkCore;
using TenantFlow.Tenants.Contracts;
using TenantFlow.Tenants.Domain.Tenants;
using TenantFlow.Tenants.Infrastructure.Persistence;

namespace TenantFlow.Tenants.Infrastructure;

/// <summary>The Tenants module's answer to <see cref="ITenantDirectory"/>: a read of status only, nothing else leaves the module.</summary>
internal sealed class TenantDirectory(TenantsDbContext context) : ITenantDirectory
{
    public async Task<IReadOnlyDictionary<Guid, TenantAvailability>> GetAvailabilityAsync(
        IReadOnlyCollection<Guid> tenantIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);
        if (tenantIds.Count == 0)
        {
            return new Dictionary<Guid, TenantAvailability>();
        }

        var rows = await context.Tenants
            .AsNoTracking()
            .Where(tenant => tenantIds.Contains(tenant.Id))
            .Select(tenant => new { tenant.Id, tenant.Status })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.Id, row => ToAvailability(row.Status));
    }

    public async Task<IReadOnlyList<Guid>> ListIdsAsync(Guid? after, int limit, CancellationToken cancellationToken)
    {
        var tenants = context.Tenants.AsNoTracking();
        if (after is { } last)
        {
            tenants = tenants.Where(tenant => tenant.Id.CompareTo(last) > 0);
        }

        return await tenants.OrderBy(tenant => tenant.Id).Select(tenant => tenant.Id).Take(limit).ToListAsync(cancellationToken);
    }

    private static TenantAvailability ToAvailability(TenantStatus status) => status switch
    {
        TenantStatus.Active => TenantAvailability.Active,
        TenantStatus.Suspended => TenantAvailability.Suspended,
        _ => throw new InvalidOperationException("The tenant status is not one the domain defines."),
    };
}
