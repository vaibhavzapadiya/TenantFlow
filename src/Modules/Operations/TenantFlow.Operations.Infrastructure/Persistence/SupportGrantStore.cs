using Microsoft.EntityFrameworkCore;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.BuildingBlocks.Messaging.Outbox;
using TenantFlow.Operations.Application.Abstractions;
using TenantFlow.Operations.Domain.SupportGrants;

namespace TenantFlow.Operations.Infrastructure.Persistence;

/// <summary>
/// Grants and their audit events in one transaction each. Opening is serialized per operator and tenant with an advisory
/// lock (ADR-0018), so two requests cannot both find no active grant. An access locks the grant row it relies on, so an
/// end that commits first makes the access find none.
/// </summary>
internal sealed class SupportGrantStore(OperationsDbContext db) : ISupportGrantStore
{
    public async Task<bool> TryOpenAsync(SupportGrant grant, OutgoingMessage audit, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"support:" + grant.OperatorRef + ":" + grant.TenantId.ToString("N")}, 0))",
            cancellationToken);
        if (await db.SupportGrants.AnyAsync(g => g.OperatorRef == grant.OperatorRef && g.TenantId == grant.TenantId && g.EndedAt == null && g.ExpiresAt > now, cancellationToken))
        {
            return false;
        }

        db.SupportGrants.Add(grant);
        db.OutboxMessages.Add(OutboxMessage.Create(audit, grant.TenantId, now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return true;
    }

    public async Task<SupportGrant?> FindAsync(Guid grantId, CancellationToken cancellationToken) =>
        await db.SupportGrants.AsNoTracking().SingleOrDefaultAsync(g => g.Id == grantId, cancellationToken);

    public async Task<bool> EndAsync(Guid grantId, string endedBy, DateTimeOffset now, OutgoingMessage audit, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var grant = await db.SupportGrants.FromSql($"SELECT * FROM operations.support_grants WHERE id = {grantId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        if (grant is null || !grant.IsActive(now))
        {
            return false;
        }

        db.Entry(grant).Property(g => g.EndedAt).CurrentValue = now;
        db.Entry(grant).Property(g => g.EndedBy).CurrentValue = endedBy;
        db.OutboxMessages.Add(OutboxMessage.Create(audit, grant.TenantId, now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return true;
    }

    public async Task<SupportGrant?> RecordAccessAsync(Guid tenantId, string operatorRef, DateTimeOffset now, Func<SupportGrant, OutgoingMessage> audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var grant = await db.SupportGrants
            .FromSql($"SELECT * FROM operations.support_grants WHERE tenant_id = {tenantId} AND operator_ref = {operatorRef} AND ended_at IS NULL AND expires_at > {now} ORDER BY expires_at DESC LIMIT 1 FOR SHARE")
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (grant is null)
        {
            return null;
        }

        db.OutboxMessages.Add(OutboxMessage.Create(audit(grant), tenantId, now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return grant;
    }
}
