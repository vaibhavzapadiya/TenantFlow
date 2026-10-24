using Microsoft.EntityFrameworkCore;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.IdentityAccess.Domain.Memberships;

namespace TenantFlow.IdentityAccess.Infrastructure.Persistence;

/// <summary>Answers <see cref="ISupportMemberView"/> for one tenant; issuer and subject are never selected.</summary>
internal sealed class SupportMemberView(IdentityAccessDbContext db) : ISupportMemberView
{
    public async Task<IReadOnlyList<SupportMember>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = new TenantId(tenantId);
        var rows = await db.Memberships.AsNoTracking()
            .Where(m => m.TenantId == tenant)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.Id, m.Role, m.Status, m.CreatedAt, m.UpdatedAt })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(static r => new SupportMember(r.Id.Value, r.Role.ToName(), r.Status.ToName(), r.CreatedAt, r.UpdatedAt))];
    }
}
