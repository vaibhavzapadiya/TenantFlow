using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.BuildingBlocks.Messaging.Outbox;
using TenantFlow.BuildingBlocks.Messaging.Timers;
using TenantFlow.BuildingBlocks.Persistence;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.Payments.Application.Abstractions;
using TenantFlow.Payments.Application.Observability;
using TenantFlow.Payments.Application.Payments.Dtos;
using TenantFlow.Payments.Application.Payments.Services;
using TenantFlow.Payments.Application.Refunds.Dtos;
using TenantFlow.Payments.Application.Refunds.Services;
using TenantFlow.Payments.Domain.Payments;
using TenantFlow.Payments.Domain.Refunds;

namespace TenantFlow.Payments.Infrastructure.Persistence;

/// <summary>
/// Every operation filters by the tenant context's id and runs in a transaction that sets it for row-level security
/// (ADR-0011): two independent controls. Reading <c>context.TenantId</c> throws when no tenant is set, so nothing here
/// touches data without one. Outcomes are counted in <see cref="PaymentsMetrics"/> after their transaction commits.
/// </summary>
internal sealed partial class PaymentStore(PaymentsDbContext db, ITenantContext tenant, TimeProvider clock, PaymentsMetrics metrics, ILogger<PaymentStore> logger) : IPaymentStore
{
    private const string AppliedOutcome = "applied";
    private const string IgnoredOutcome = "ignored";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string ScheduleTimerSql = WorkflowTimerSql.Schedule(PaymentsDbContext.Schema);

    public async Task<IdempotentRequest?> FindRequestAsync(string operation, string key, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        var row = await db.IdempotencyKeys.AsNoTracking()
            .SingleOrDefaultAsync(k => k.TenantId == tenantId && k.Operation == operation && k.Key == key, cancellationToken);
        return row is null ? null : new IdempotentRequest(row.RequestHash, row.ResponseBody);
    }

    public async Task<StartResult> TryStartAsync(Payment payment, IdempotencyKeyRef key, string requestHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payment);
        ArgumentNullException.ThrowIfNull(key);
        var tenantId = tenant.TenantId;
        if (payment.TenantId != tenantId)
        {
            // A programming error: a payment is started for the caller's tenant only.
            throw new InvalidOperationException("A payment can only be started in the tenant in the current context.");
        }

        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        // The key first: a concurrent request with the same key waits on it here, then finds it taken.
        var claimed = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO payments.idempotency_keys (tenant_id, operation, key, request_hash, payment_id, created_at)
            VALUES ({tenantId}, {key.Operation}, {key.Key}, {requestHash}, {payment.Id}, {payment.CreatedAt})
            ON CONFLICT (tenant_id, operation, key) DO NOTHING
            """,
            cancellationToken);
        if (claimed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return StartResult.KeyTaken;
        }

        var inserted = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO payments.payments (id, tenant_id, order_id, owner_membership_id, amount, currency, status, provider, payment_method, created_at, updated_at)
            VALUES ({payment.Id}, {tenantId}, {payment.OrderId}, {payment.OwnerMembershipId}, {payment.Amount}, {payment.Currency}, {payment.Status.ToName()},
                    {payment.Provider}, {payment.PaymentMethod}, {payment.CreatedAt}, {payment.UpdatedAt})
            ON CONFLICT (tenant_id, order_id) DO NOTHING
            """,
            cancellationToken);
        if (inserted == 0)
        {
            // The order already has a payment: the key is not claimed either, so it stays free for a correct request.
            await transaction.RollbackAsync(cancellationToken);
            return StartResult.PaymentExists;
        }

        // Reconciliation is due unless the outcome is final by then: a crash after this commit can never strand the payment.
        await db.Database.ExecuteSqlRawAsync(
            ScheduleTimerSql,
            [Guid.CreateVersion7(payment.CreatedAt), tenantId, PaymentReconciliation.TimerKind, payment.Id, payment.CreatedAt + PaymentReconciliation.FirstCheckAfter],
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return StartResult.Started;
    }

    public async Task<PaymentDetails> ApplyAsync(Guid paymentId, PaymentTrigger trigger, string? providerPaymentId, string? failureCode, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var now = clock.GetUtcNow();
        db.ChangeTracker.Clear();
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        var payment = await LockAsync(tenantId, paymentId, cancellationToken)
            ?? throw new InvalidOperationException("The payment to apply an outcome to does not exist in this tenant.");
        var changed = await ApplyLockedAsync(payment, trigger, providerPaymentId, failureCode, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        RecordIfChanged(payment, changed);
        return PaymentDetails.From(payment);
    }

    public async Task<Payment?> FindAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        return await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.TenantId == tenantId && p.Id == paymentId, cancellationToken);
    }

    public async Task<WebhookOutcome> ApplyWebhookAsync(Guid tenantId, string provider, ProviderWebhookEvent webhook, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        var now = clock.GetUtcNow();
        db.ChangeTracker.Clear();
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        // The event id first: of two deliveries of one event exactly one inserts it, and only that one goes on.
        var first = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO payments.webhook_events (provider, event_id, tenant_id, payment_id, outcome, received_at)
            VALUES ({provider}, {webhook.EventId}, {tenantId}, {webhook.SubjectId}, {AppliedOutcome}, {now})
            ON CONFLICT (provider, event_id) DO NOTHING
            """,
            cancellationToken);
        if (first == 0)
        {
            return WebhookOutcome.Duplicate;
        }

        if (webhook.IsRefund)
        {
            return await ApplyRefundWebhookAsync(tenantId, provider, webhook, now, transaction, cancellationToken);
        }

        // The metadata named this tenant and payment; the stored payment must agree on provider and provider reference,
        // or the event acts on nothing (SPEC-0006 criterion 10). The transaction rolls back on dispose.
        var payment = await LockAsync(tenantId, webhook.SubjectId, cancellationToken);
        if (payment is null || payment.Provider != provider || (payment.ProviderPaymentId is not null && payment.ProviderPaymentId != webhook.ProviderReference))
        {
            return WebhookOutcome.NotFound;
        }

        var allowed = PaymentStateMachine.Next(payment.Status, webhook.Outcome) is not null;
        var changed = await ApplyLockedAsync(payment, webhook.Outcome, webhook.ProviderReference, webhook.FailureCode, now, cancellationToken);
        if (!allowed)
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE payments.webhook_events SET outcome = {IgnoredOutcome} WHERE provider = {provider} AND event_id = {webhook.EventId}",
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        RecordIfChanged(payment, changed);
        return allowed ? WebhookOutcome.Applied : WebhookOutcome.Ignored;
    }

    public async Task<Payment?> FindByOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        return await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.TenantId == tenantId && p.OrderId == orderId, cancellationToken);
    }

    public async Task<bool> TryStartChargeAsync(Payment payment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payment);
        var tenantId = tenant.TenantId;
        if (payment.TenantId != tenantId || payment.SubscriptionId is null)
        {
            // A programming error: a charge is started in the current tenant, for a subscription.
            throw new InvalidOperationException("A subscription charge can only be started in the tenant in the current context.");
        }

        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        // The unique attempt decides a repeated or concurrent renewal: exactly one insert wins, the rest find its payment.
        var inserted = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO payments.payments (id, tenant_id, subscription_id, billing_period, charge_attempt, owner_membership_id, amount, currency, status,
                                           provider, payment_method, created_at, updated_at)
            VALUES ({payment.Id}, {tenantId}, {payment.SubscriptionId}, {payment.BillingPeriod}, {payment.ChargeAttempt}, {payment.OwnerMembershipId},
                    {payment.Amount}, {payment.Currency}, {payment.Status.ToName()}, {payment.Provider}, {payment.PaymentMethod}, {payment.CreatedAt}, {payment.UpdatedAt})
            ON CONFLICT (tenant_id, subscription_id, billing_period, charge_attempt) WHERE subscription_id IS NOT NULL DO NOTHING
            """,
            cancellationToken);
        if (inserted == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await db.Database.ExecuteSqlRawAsync(
            ScheduleTimerSql,
            [Guid.CreateVersion7(payment.CreatedAt), tenantId, PaymentReconciliation.TimerKind, payment.Id, payment.CreatedAt + PaymentReconciliation.FirstCheckAfter],
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<Payment?> FindChargeAsync(Guid subscriptionId, int billingPeriod, int chargeAttempt, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        return await db.Payments.AsNoTracking().SingleOrDefaultAsync(
            p => p.TenantId == tenantId && p.SubscriptionId == subscriptionId && p.BillingPeriod == billingPeriod && p.ChargeAttempt == chargeAttempt,
            cancellationToken);
    }

    // Locked for the rest of the transaction: a webhook, the creating request and reconciliation apply one at a time, each
    // to the state the previous one committed, and the state table decides (SPEC-0006 criterion 9).
    private Task<Payment?> LockAsync(Guid tenantId, Guid paymentId, CancellationToken cancellationToken) =>
        db.Payments
            .FromSql($"SELECT * FROM payments.payments WHERE tenant_id = {tenantId} AND id = {paymentId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    // Returns whether the payment moved to a new status.
    private async Task<bool> ApplyLockedAsync(Payment payment, PaymentTrigger trigger, string? providerPaymentId, string? failureCode, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var changed = payment.Apply(trigger, providerPaymentId, failureCode, now);
        await db.SaveChangesAsync(cancellationToken);
        if (changed && PaymentEvents.For(payment) is { } message)
        {
            // Same transaction as the state change: the event exists if and only if the change committed (ADR-0021).
            var row = OutboxMessage.Create(message, payment.TenantId, now);
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO payments.outbox_messages (id, tenant_id, type, version, payload, correlation_id, traceparent, occurred_at)
                VALUES ({row.Id}, {row.TenantId}, {row.Type}, {row.Version}, {row.Payload}::jsonb, {row.CorrelationId}, {row.TraceParent}, {row.OccurredAt})
                """,
                cancellationToken);
        }

        // Every unfinished key of this payment answers with what is now known (its creating request finished, or was
        // interrupted after claiming the key); a finished key keeps its first answer.
        var body = JsonSerializer.Serialize(PaymentDetails.From(payment), Json);
        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE payments.idempotency_keys SET response_body = {body}::jsonb, completed_at = {now}
            WHERE tenant_id = {payment.TenantId} AND payment_id = {payment.Id} AND refund_id IS NULL AND completed_at IS NULL
            """,
            cancellationToken);
        return changed;
    }

    private void RecordIfChanged(Payment payment, bool changed)
    {
        if (changed)
        {
            metrics.RecordPayment(payment.Status);
        }
    }

    private void RecordIfChanged(Refund refund, bool changed)
    {
        if (changed)
        {
            metrics.RecordRefund(refund.Status);
        }
    }

    public async Task<RefundStart> TryStartRefundAsync(Refund refund, IdempotencyKeyRef? key, string? requestHash, OutgoingMessage? audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(refund);
        var tenantId = tenant.TenantId;
        if (refund.TenantId != tenantId)
        {
            throw new InvalidOperationException("A refund can only be started in the tenant in the current context.");
        }

        db.ChangeTracker.Clear();
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        if (key is not null)
        {
            // The key first: a concurrent request with the same key waits on it here, then finds it taken.
            var claimed = await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO payments.idempotency_keys (tenant_id, operation, key, request_hash, payment_id, refund_id, created_at)
                VALUES ({tenantId}, {key.Operation}, {key.Key}, {requestHash}, {refund.PaymentId}, {refund.Id}, {refund.CreatedAt})
                ON CONFLICT (tenant_id, operation, key) DO NOTHING
                """,
                cancellationToken);
            if (claimed == 0)
            {
                return RefundStart.KeyTaken;
            }
        }

        // The payment is locked until commit: every refund of it is checked against the refunds committed before it
        // (SPEC-0007 criterion 10), so two concurrent refunds can never together pass the captured amount.
        var payment = await LockAsync(tenantId, refund.PaymentId, cancellationToken);
        if (payment is not { Status: PaymentStatus.Succeeded })
        {
            return RefundStart.NotRefundable;
        }

        if (refund.Kind == Refund.CancellationKind)
        {
            if (await db.Refunds.AnyAsync(r => r.TenantId == tenantId && r.PaymentId == payment.Id && r.Kind == Refund.CancellationKind && r.Status != PaymentStatus.Failed, cancellationToken))
            {
                return RefundStart.CancellationExists;
            }

            // What is left is known only once every other refund has settled: a pending one may still fail (review fix R4).
            if (await db.Refunds.AnyAsync(r => r.TenantId == tenantId && r.PaymentId == payment.Id && r.Status != PaymentStatus.Failed && r.Status != PaymentStatus.Succeeded, cancellationToken))
            {
                return RefundStart.RefundsUnsettled;
            }
        }

        // Refunds that have not failed hold their amount: a pending one may still succeed.
        var held = await db.Refunds
            .Where(r => r.TenantId == tenantId && r.PaymentId == payment.Id && r.Status != PaymentStatus.Failed)
            .SumAsync(static r => r.Amount, cancellationToken);
        var amount = refund.Kind == Refund.CancellationKind ? payment.Amount - held : refund.Amount;
        if (amount <= 0 || held + amount > payment.Amount)
        {
            return RefundStart.ExceedsCaptured;
        }

        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO payments.refunds (id, tenant_id, payment_id, order_id, amount, currency, status, kind, reason, requested_by_membership_id, provider, created_at, updated_at)
            VALUES ({refund.Id}, {tenantId}, {refund.PaymentId}, {refund.OrderId}, {amount}, {refund.Currency}, {refund.Status.ToName()}, {refund.Kind}, {refund.Reason},
                    {refund.RequestedByMembershipId}, {refund.Provider}, {refund.CreatedAt}, {refund.UpdatedAt})
            """,
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            ScheduleTimerSql,
            [Guid.CreateVersion7(refund.CreatedAt), tenantId, RefundReconciliation.TimerKind, refund.Id, refund.CreatedAt + PaymentReconciliation.FirstCheckAfter],
            cancellationToken);
        if (audit is not null)
        {
            // Same transaction: the audit event exists if and only if the refund committed (ADR-0026).
            var row = OutboxMessage.Create(audit, tenantId, refund.CreatedAt);
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO payments.outbox_messages (id, tenant_id, type, version, payload, correlation_id, traceparent, occurred_at)
                VALUES ({row.Id}, {row.TenantId}, {row.Type}, {row.Version}, {row.Payload}::jsonb, {row.CorrelationId}, {row.TraceParent}, {row.OccurredAt})
                """,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return RefundStart.Started;
    }

    public async Task<RefundDetails> ApplyRefundAsync(Guid refundId, PaymentTrigger trigger, string? providerRefundId, string? failureCode, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var now = clock.GetUtcNow();
        db.ChangeTracker.Clear();
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        var refund = await db.Refunds.SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == refundId, cancellationToken)
            ?? throw new InvalidOperationException("The refund to apply an outcome to does not exist in this tenant.");
        var payment = await LockAsync(tenantId, refund.PaymentId, cancellationToken)
            ?? throw new InvalidOperationException("The refunded payment does not exist in this tenant.");
        // Read again under the payment's lock: another outcome of this refund may have committed meanwhile.
        await db.Entry(refund).ReloadAsync(cancellationToken);
        var changed = await ApplyRefundLockedAsync(refund, payment, trigger, providerRefundId, failureCode, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        RecordIfChanged(refund, changed);
        return RefundDetails.From(refund);
    }

    public async Task<Refund?> FindRefundAsync(Guid refundId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        return await db.Refunds.AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == refundId, cancellationToken);
    }

    public async Task<Refund?> FindCancellationRefundAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        return await db.Refunds.AsNoTracking()
            .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.PaymentId == paymentId && r.Kind == Refund.CancellationKind && r.Status != PaymentStatus.Failed, cancellationToken);
    }

    public async Task<IReadOnlyList<Refund>> ListRefundsAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        return await db.Refunds.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.PaymentId == paymentId)
            .OrderBy(static r => r.CreatedAt).ThenBy(static r => r.Id)
            .ToListAsync(cancellationToken);
    }

    // A refund webhook (SPEC-0007 criterion 12): the event id is recorded; the refund must exist in the signed tenant with
    // the same provider and provider reference, as for payments.
    private async Task<WebhookOutcome> ApplyRefundWebhookAsync(Guid tenantId, string provider, ProviderWebhookEvent webhook, DateTimeOffset now,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        var refund = await db.Refunds.SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == webhook.SubjectId, cancellationToken);
        var payment = refund is null ? null : await LockAsync(tenantId, refund.PaymentId, cancellationToken);
        if (refund is null || payment is null || refund.Provider != provider || (refund.ProviderRefundId is not null && refund.ProviderRefundId != webhook.ProviderReference))
        {
            return WebhookOutcome.NotFound;
        }

        await db.Entry(refund).ReloadAsync(cancellationToken);
        var reversed = refund.Status == PaymentStatus.Succeeded && webhook.Outcome == PaymentTrigger.Failed;
        if (reversed)
        {
            // A provider can fail a refund after reporting success: the money did not reach the customer, but the state
            // table keeps a succeeded refund final. Nobody may miss it (review fix R12).
            LogRefundReversed(logger, refund.Id, refund.PaymentId, provider);
        }

        var allowed = PaymentStateMachine.Next(refund.Status, webhook.Outcome) is not null;
        var changed = await ApplyRefundLockedAsync(refund, payment, webhook.Outcome, webhook.ProviderReference, webhook.FailureCode, now, cancellationToken);
        if (!allowed)
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE payments.webhook_events SET outcome = {IgnoredOutcome} WHERE provider = {provider} AND event_id = {webhook.EventId}",
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        RecordIfChanged(refund, changed);
        if (reversed)
        {
            // A counter as well as the log, so an alert can page on it (PY-A25, ADR-0029).
            metrics.RecordRefundReversed();
        }

        return allowed ? WebhookOutcome.Applied : WebhookOutcome.Ignored;
    }

    // With the payment locked: the refund moves by the state table; a success is counted in the payment's refunded amount
    // in the same transaction as its event; every unfinished key of the refund answers with what is now known.
    private async Task<bool> ApplyRefundLockedAsync(Refund refund, Payment payment, PaymentTrigger trigger, string? providerRefundId, string? failureCode, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var changed = refund.Apply(trigger, providerRefundId, failureCode, now);
        if (changed && refund.Status == PaymentStatus.Succeeded)
        {
            payment.RecordRefund(refund.Amount, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        if (changed && RefundEvents.For(refund, payment) is { } message)
        {
            var row = OutboxMessage.Create(message, payment.TenantId, now);
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO payments.outbox_messages (id, tenant_id, type, version, payload, correlation_id, traceparent, occurred_at)
                VALUES ({row.Id}, {row.TenantId}, {row.Type}, {row.Version}, {row.Payload}::jsonb, {row.CorrelationId}, {row.TraceParent}, {row.OccurredAt})
                """,
                cancellationToken);
        }

        var body = JsonSerializer.Serialize(RefundDetails.From(refund), Json);
        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE payments.idempotency_keys SET response_body = {body}::jsonb, completed_at = {now}
            WHERE tenant_id = {refund.TenantId} AND refund_id = {refund.Id} AND completed_at IS NULL
            """,
            cancellationToken);
        return changed;
    }

    [LoggerMessage(EventId = 5301, Level = LogLevel.Critical, Message = "Refund {RefundId} of payment {PaymentId} was reported failed by {Provider} after it succeeded; the customer was not refunded and it needs review")]
    private static partial void LogRefundReversed(ILogger logger, Guid refundId, Guid paymentId, string provider);
}
