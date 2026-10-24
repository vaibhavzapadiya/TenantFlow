using Microsoft.EntityFrameworkCore;
using TenantFlow.BuildingBlocks.Persistence;
using TenantFlow.Notifications.Application.Abstractions;
using TenantFlow.Notifications.Domain.Notifications;

namespace TenantFlow.Notifications.Infrastructure.Persistence;

/// <summary>
/// Every write runs in a transaction that sets the message's tenant for row-level security (ADR-0011). The processed-message
/// row is inserted first: its key is the consumer and the message id, so of two deliveries of one message exactly one
/// inserts it, and only that one writes the notification (ADR-0021).
/// </summary>
internal sealed class NotificationStore(NotificationsDbContext db, TimeProvider clock) : INotificationStore
{
    public async Task<bool> RecordAsync(Guid tenantId, Notification notification, string consumer, Guid messageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (notification.TenantId != tenantId)
        {
            // A programming error: a notification is recorded in the tenant of the message that caused it.
            throw new InvalidOperationException("A notification can only be recorded in the tenant of its message.");
        }

        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        var first = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO notifications.processed_messages (consumer, message_id, tenant_id, processed_at)
            VALUES ({consumer}, {messageId}, {tenantId}, {clock.GetUtcNow()})
            ON CONFLICT (consumer, message_id) DO NOTHING
            """,
            cancellationToken);
        if (first == 0)
        {
            // Already processed: the transaction is rolled back on dispose and nothing is written.
            return false;
        }

        db.Notifications.Add(notification);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
