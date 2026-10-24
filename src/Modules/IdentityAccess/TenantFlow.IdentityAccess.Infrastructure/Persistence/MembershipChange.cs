using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.BuildingBlocks.Messaging.Outbox;
using TenantFlow.IdentityAccess.Application.Abstractions;
using TenantFlow.IdentityAccess.Domain.Auditing;
using TenantFlow.IdentityAccess.Domain.Memberships;

namespace TenantFlow.IdentityAccess.Infrastructure.Persistence;

/// <summary>
/// One tenant's membership change, inside a transaction that already holds the tenant's advisory lock. Every query
/// carries the tenant id, so another tenant's memberships are unreachable from here.
/// </summary>
internal sealed class MembershipChange(IdentityAccessDbContext context, IDbContextTransaction transaction, TenantId tenantId) : IMembershipChange
{
    private const string UniqueViolation = "23505";

    public Task<Membership?> FindActiveAsync(MembershipId id, CancellationToken cancellationToken) =>
        context.Memberships.SingleOrDefaultAsync(
            m => m.TenantId == tenantId && m.Id == id && m.Status == MembershipStatus.Active, cancellationToken);

    public Task<Membership?> FindActiveAsync(string issuer, string subject, CancellationToken cancellationToken) =>
        context.Memberships.SingleOrDefaultAsync(
            m => m.TenantId == tenantId && m.Issuer == issuer && m.Subject == subject && m.Status == MembershipStatus.Active,
            cancellationToken);

    public Task<int> CountActiveAdministratorsAsync(CancellationToken cancellationToken) =>
        context.Memberships.CountAsync(
            m => m.TenantId == tenantId && m.Status == MembershipStatus.Active && m.Role == MembershipRole.TenantAdmin, cancellationToken);

    public void Add(Membership membership)
    {
        EnsureThisTenant(membership.TenantId);
        context.Memberships.Add(membership);
    }

    public void Append(MembershipAuditRecord record)
    {
        EnsureThisTenant(record.TenantId);
        context.AuditRecords.Add(record);
    }

    // The envelope's tenant is the one this change was started for, so a message can never name another tenant.
    public void Announce(OutgoingMessage message, DateTimeOffset occurredAt) =>
        context.OutboxMessages.Add(OutboxMessage.Create(message, tenantId.Value, occurredAt));

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        try
        {
            // The change, its audit records and its outbox messages are one SaveChanges, inside the lock's transaction.
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // The lock makes this unreachable through the service; the unique index is the backstop.
            throw new AppException(MembershipErrors.AlreadyExists, exception);
        }
    }

    public ValueTask DisposeAsync() => transaction.DisposeAsync();

    private void EnsureThisTenant(TenantId other)
    {
        if (other != tenantId)
        {
            // A programming error: this change was started for one tenant and must never write another's rows.
            throw new InvalidOperationException("A membership change can only write rows of the tenant it was started for.");
        }
    }
}
