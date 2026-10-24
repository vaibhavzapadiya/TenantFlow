using Microsoft.EntityFrameworkCore;
using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.BuildingBlocks.Messaging.Outbox;
using TenantFlow.BuildingBlocks.Messaging.Timers;
using TenantFlow.BuildingBlocks.Persistence;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.Orders.Application.Abstractions;
using TenantFlow.Orders.Application.Orders.Services;
using TenantFlow.Orders.Domain.Orders;

namespace TenantFlow.Orders.Infrastructure.Persistence;

/// <summary>
/// Every operation filters by the tenant context's id and runs in a transaction that sets it for row-level security
/// (ADR-0011): two independent controls. Reading <c>context.TenantId</c> throws when no tenant is set, so nothing here
/// touches data without one.
/// </summary>
internal sealed class OrderStore(OrdersDbContext db, ITenantContext tenant, TimeProvider clock) : IOrderStore
{
    private const string VersionColumn = "xmin";

    private static readonly string ScheduleTimerSql = WorkflowTimerSql.Schedule(OrdersDbContext.Schema);

    public async Task<OrderRecord> AddAsync(Order order, CancellationToken cancellationToken)
    {
        if (order.TenantId != tenant.TenantId)
        {
            // A programming error: an order is created for the caller's tenant only.
            throw new InvalidOperationException("An order can only be added to the tenant in the current context.");
        }

        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenant.TenantId, cancellationToken);
        db.Orders.Add(order);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Record(order);
    }

    public async Task<OrderRecord?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        // A fresh read: forget what this scope tracked, so a transition written with SQL is never hidden by a stale entity.
        db.ChangeTracker.Clear();
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        var order = await db.Orders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.TenantId == tenantId && o.Id == id, cancellationToken);
        return order is null ? null : Record(order);
    }

    public async Task<OrderRecord> SaveAsync(Order order, uint expectedVersion, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (order.TenantId != tenantId)
        {
            throw new InvalidOperationException("An order can only be saved by the tenant that owns it.");
        }

        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        // The order is tracked from FindAsync in this scope. The compare-and-set uses the version the caller read.
        db.Entry(order).Property<uint>(VersionColumn).OriginalValue = expectedVersion;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new AppException(OrderErrors.VersionConflict, exception);
        }

        await transaction.CommitAsync(cancellationToken);
        return Record(order);
    }

    public async Task<bool> TryTransitionAsync(Guid id, OrderStatus expected, OrderStatus target, string? rejectionReason, uint? expectedVersion, OutgoingMessage? message, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var (expectedName, targetName, now) = (expected.ToName(), target.ToName(), clock.GetUtcNow());
        // xid has no ordering or numeric cast in PostgreSQL; its text form compares exactly.
        var version = expectedVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        var changed = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE orders.orders SET status = {targetName}, rejection_reason = {rejectionReason}, updated_at = {now}
            WHERE tenant_id = {tenantId} AND id = {id} AND status = {expectedName} AND ({version}::text IS NULL OR xmin::text = {version})
              AND ({targetName} <> {OrderStatusNames.Cancelled} OR payment_started_at IS NULL)
            """,
            cancellationToken);
        if (changed == 1 && message is not null)
        {
            // Same transaction as the state change: the event exists if and only if the transition committed (ADR-0021).
            var row = OutboxMessage.Create(message, tenantId, now);
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO orders.outbox_messages (id, tenant_id, type, version, payload, correlation_id, traceparent, occurred_at)
                VALUES ({row.Id}, {row.TenantId}, {row.Type}, {row.Version}, {row.Payload}::jsonb, {row.CorrelationId}, {row.TraceParent}, {row.OccurredAt})
                """,
                cancellationToken);
        }

        if (changed == 1 && OrderDeadlines.For(target) is { } deadline)
        {
            // Same transaction: the deadline exists if and only if the order reached the state it guards.
            await db.Database.ExecuteSqlRawAsync(ScheduleTimerSql, [Guid.CreateVersion7(now), tenantId, deadline.Kind, id, now + deadline.After], cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return changed == 1;
    }

    public async Task<bool> TryRequestCancellationAsync(Guid id, OrderStatus expected, string reason, Guid requestedBy, OutgoingMessage audit, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var now = clock.GetUtcNow();
        var (deadlineKind, after) = OrderDeadlines.For(OrderStatus.CancellationPending)!.Value;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        var changed = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE orders.orders
            SET status = {OrderStatusNames.CancellationPending}, cancellation_reason = {reason}, cancellation_requested_by = {requestedBy}, updated_at = {now}
            WHERE tenant_id = {tenantId} AND id = {id} AND status = {expected.ToName()}
            """,
            cancellationToken);
        if (changed == 1)
        {
            // Same transaction: the refund job exists if and only if the cancellation committed.
            await db.Database.ExecuteSqlRawAsync(ScheduleTimerSql, [Guid.CreateVersion7(now), tenantId, deadlineKind, id, now + after], cancellationToken);
            // And so does its audit event (ADR-0026).
            var row = OutboxMessage.Create(audit, tenantId, now);
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO orders.outbox_messages (id, tenant_id, type, version, payload, correlation_id, traceparent, occurred_at)
                VALUES ({row.Id}, {row.TenantId}, {row.Type}, {row.Version}, {row.Payload}::jsonb, {row.CorrelationId}, {row.TraceParent}, {row.OccurredAt})
                """,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return changed == 1;
    }

    public async Task<bool> TryBeginPaymentAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var now = clock.GetUtcNow();
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        var changed = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE orders.orders SET payment_started_at = {now}, updated_at = {now}
            WHERE tenant_id = {tenantId} AND id = {id} AND status = {OrderStatusNames.PendingPayment} AND payment_started_at IS NULL
            """,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return changed == 1;
    }

    public async Task<MessageEffect> ApplyMessageAsync(Guid id, OrderTrigger trigger, string consumer, Guid messageId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var now = clock.GetUtcNow();
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        // The message id first: of two deliveries of one message exactly one inserts it, and only that one goes on.
        var first = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO orders.processed_messages (consumer, message_id, tenant_id, processed_at)
            VALUES ({consumer}, {messageId}, {tenantId}, {now})
            ON CONFLICT (consumer, message_id) DO NOTHING
            """,
            cancellationToken);
        // Locked, so a concurrent cancel or another message waits and then sees this one's result.
        var current = await db.Database.SqlQuery<string>(
            $"SELECT status AS \"Value\" FROM orders.orders WHERE tenant_id = {tenantId} AND id = {id} FOR UPDATE").ToListAsync(cancellationToken);
        if (current.Count == 0)
        {
            // Rolled back on dispose: an event for an order this tenant does not have records nothing.
            return new MessageEffect(MessageEffectKind.OrderMissing, null);
        }

        var status = OrderStatusNames.Parse(current[0]);
        if (first == 0)
        {
            return new MessageEffect(MessageEffectKind.Duplicate, status);
        }

        var next = OrderStateMachine.Next(status, trigger);
        if (next is { } target)
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE orders.orders SET status = {target.ToName()}, updated_at = {now} WHERE tenant_id = {tenantId} AND id = {id}",
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return next is null ? new MessageEffect(MessageEffectKind.Ignored, status) : new MessageEffect(MessageEffectKind.Applied, next);
    }

    private OrderRecord Record(Order order) => new(order, db.Entry(order).Property<uint>(VersionColumn).CurrentValue);
}
