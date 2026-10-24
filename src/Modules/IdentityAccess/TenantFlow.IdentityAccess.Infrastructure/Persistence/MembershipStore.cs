using Microsoft.EntityFrameworkCore;
using TenantFlow.IdentityAccess.Application.Abstractions;
using TenantFlow.IdentityAccess.Domain.Memberships;

namespace TenantFlow.IdentityAccess.Infrastructure.Persistence;

internal sealed class MembershipStore(IdentityAccessDbContext context) : IMembershipStore
{
    public async Task<IMembershipChange> BeginChangeAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // One advisory lock per tenant, held until this transaction ends (the lock belongs to the
            // transaction, so a pooled connection can never carry it to the next request). Changes to the same tenant
            // queue here; changes to other tenants do not wait. A hash collision only makes two tenants share a queue.
            // The tenant id travels as a parameter, never as text in the statement.
            var key = tenantId.Value.ToString("D");
            await context.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
            return new MembershipChange(context, transaction, tenantId);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    public Task<Membership?> FindActiveAsync(TenantId tenantId, string issuer, string subject, CancellationToken cancellationToken) =>
        context.Memberships
            .AsNoTracking()
            .SingleOrDefaultAsync(
                m => m.TenantId == tenantId && m.Issuer == issuer && m.Subject == subject && m.Status == MembershipStatus.Active,
                cancellationToken);

    public async Task<IReadOnlyList<Membership>> ListActiveInTenantAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        await context.Memberships
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.Status == MembershipStatus.Active)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Membership>> ListActiveForIdentityAsync(string issuer, string subject, CancellationToken cancellationToken) =>
        await context.Memberships
            .AsNoTracking()
            .Where(m => m.Issuer == issuer && m.Subject == subject && m.Status == MembershipStatus.Active)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToListAsync(cancellationToken);
}
