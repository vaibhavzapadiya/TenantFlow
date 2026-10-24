using Microsoft.EntityFrameworkCore;
using TenantFlow.Audit.Application.Abstractions;
using TenantFlow.Audit.Domain.Records;
using TenantFlow.BuildingBlocks.Persistence;

namespace TenantFlow.Audit.Infrastructure.Persistence;

/// <summary>
/// Every write runs in a transaction that sets the record's tenant for row-level security (ADR-0011). The processed-message
/// row is inserted first: its key is the consumer and the message id, so of two deliveries of one message exactly one
/// inserts it, and only that one writes the record (ADR-0021). The record's own key, the message id, is the backstop.
/// </summary>
internal sealed class AuditStore(AuditDbContext db, TimeProvider clock) : IAuditStore
{
    public async Task<bool> RecordAsync(AuditRecord record, string consumer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, record.TenantId, cancellationToken);
        var first = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO audit.processed_messages (consumer, message_id, tenant_id, processed_at)
            VALUES ({consumer}, {record.Id}, {record.TenantId}, {clock.GetUtcNow()})
            ON CONFLICT (consumer, message_id) DO NOTHING
            """,
            cancellationToken);
        if (first == 0)
        {
            // Already processed: the transaction is rolled back on dispose and nothing is written.
            return false;
        }

        db.Records.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<AuditRecord>> ListAsync(Guid tenantId, AuditCursor? after, string? action, int take, CancellationToken cancellationToken)
    {
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        // Keyset on the (tenant_id, occurred_at desc, id desc) index; the row comparison orders ids as the database does.
        var records = after is null
            ? db.Records.AsQueryable()
            : db.Records.FromSql($"SELECT * FROM audit.audit_records WHERE (occurred_at, id) < ({after.OccurredAt}, {after.Id})");
        records = records.Where(r => r.TenantId == tenantId);
        if (action is not null)
        {
            records = records.Where(r => r.Action == action);
        }

        return await records.AsNoTracking().OrderByDescending(r => r.OccurredAt).ThenByDescending(r => r.Id).Take(take).ToListAsync(cancellationToken);
    }
}
