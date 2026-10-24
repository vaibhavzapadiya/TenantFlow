using Microsoft.EntityFrameworkCore;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.IdentityAccess.Domain.Memberships;

namespace TenantFlow.IdentityAccess.Infrastructure.Persistence;

/// <summary>Answers <see cref="IMembershipDirectory"/> for the tenant in the current tenant context only.</summary>
internal sealed class MembershipDirectory(IdentityAccessDbContext db, ITenantContext tenant) : IMembershipDirectory
{
    public async Task<bool> IsActiveAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        var tenantId = new TenantId(tenant.TenantId);
        if (membershipId == Guid.Empty)
        {
            return false;
        }

        var id = new MembershipId(membershipId);
        return await db.Memberships.AsNoTracking()
            .AnyAsync(m => m.TenantId == tenantId && m.Id == id && m.Status == MembershipStatus.Active, cancellationToken);
    }

    public async Task<bool> HasPermissionAsync(Guid membershipId, string permission, CancellationToken cancellationToken)
    {
        var tenantId = new TenantId(tenant.TenantId);
        if (membershipId == Guid.Empty)
        {
            return false;
        }

        var id = new MembershipId(membershipId);
        var role = await db.Memberships.AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.Id == id && m.Status == MembershipStatus.Active)
            .Select(static m => (MembershipRole?)m.Role)
            .SingleOrDefaultAsync(cancellationToken);
        return role is { } granted && RolePermissions.Has(granted, permission);
    }
}
