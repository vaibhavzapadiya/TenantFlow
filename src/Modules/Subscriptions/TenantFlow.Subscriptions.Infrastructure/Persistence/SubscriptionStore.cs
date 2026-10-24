using Microsoft.EntityFrameworkCore;
using TenantFlow.BuildingBlocks.Messaging.Timers;
using TenantFlow.BuildingBlocks.Persistence;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.Subscriptions.Application.Abstractions;
using TenantFlow.Subscriptions.Application.Subscriptions.Services;
using TenantFlow.Subscriptions.Domain.Plans;
using TenantFlow.Subscriptions.Domain.Subscriptions;

namespace TenantFlow.Subscriptions.Infrastructure.Persistence;

/// <summary>
/// Every operation filters by the tenant context's id and runs in a transaction that sets it for row-level security
/// (ADR-0011): two independent controls. Reading <c>context.TenantId</c> throws when no tenant is set, so nothing here
/// touches data without one.
/// </summary>
internal sealed class SubscriptionStore(SubscriptionsDbContext db, ITenantContext tenant) : ISubscriptionStore
{
    private static readonly string ScheduleTimerSql = WorkflowTimerSql.Schedule(SubscriptionsDbContext.Schema);

    public async Task AddPlanAsync(Plan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var tenantId = tenant.TenantId;
        if (plan.TenantId != tenantId)
        {
            throw new InvalidOperationException("A plan can only be added in the tenant in the current context.");
        }

        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        db.Plans.Add(plan);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Plan>> ListPlansAsync(int limit, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        return await db.Plans.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.Active)
            .OrderBy(static p => p.CreatedAt).ThenBy(static p => p.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<Plan?> FindPlanAsync(Guid planId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        return await db.Plans.AsNoTracking().SingleOrDefaultAsync(p => p.TenantId == tenantId && p.Id == planId, cancellationToken);
    }

    public async Task<bool> TryStartAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        var tenantId = tenant.TenantId;
        if (subscription.TenantId != tenantId)
        {
            // A programming error: a subscription is started in the caller's tenant only.
            throw new InvalidOperationException("A subscription can only be started in the tenant in the current context.");
        }

        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        // The partial unique index decides a concurrent second subscribe: exactly one insert wins.
        var inserted = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO subscriptions.subscriptions (id, tenant_id, plan_id, subscriber_membership_id, status, payment_method, amount, currency, interval,
                                                     anchor_at, period, period_end, failed_attempts, cancel_at_period_end, created_at, updated_at)
            VALUES ({subscription.Id}, {tenantId}, {subscription.PlanId}, {subscription.SubscriberMembershipId}, {subscription.Status.ToName()},
                    {subscription.PaymentMethod}, {subscription.Amount}, {subscription.Currency}, {subscription.Interval.ToName()}, {subscription.AnchorAt},
                    {subscription.Period}, {subscription.PeriodEnd}, {subscription.FailedAttempts}, {subscription.CancelAtPeriodEnd},
                    {subscription.CreatedAt}, {subscription.UpdatedAt})
            ON CONFLICT (tenant_id, subscriber_membership_id, plan_id) WHERE status <> 'cancelled' DO NOTHING
            """,
            cancellationToken);
        if (inserted == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        // The first charge (or the trial's end) is due at the period end; it exists if and only if the subscription does.
        await db.Database.ExecuteSqlRawAsync(
            ScheduleTimerSql,
            [Guid.CreateVersion7(subscription.CreatedAt), tenantId, SubscriptionRenewals.TimerKind, subscription.Id, subscription.PeriodEnd],
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<Subscription?> FindAsync(Guid subscriptionId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        return await db.Subscriptions.AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == subscriptionId, cancellationToken);
    }

    public async Task<bool> SaveCancellationAsync(Subscription subscription, SubscriptionStatus expected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        var updated = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE subscriptions.subscriptions
            SET status = {subscription.Status.ToName()}, cancel_at_period_end = {subscription.CancelAtPeriodEnd}, cancel_reason = {subscription.CancelReason},
                updated_at = {subscription.UpdatedAt}
            WHERE tenant_id = {tenantId} AND id = {subscription.Id} AND status = {expected.ToName()} AND NOT cancel_at_period_end
            """,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated == 1;
    }

    public async Task<bool> SaveRenewalAsync(Subscription subscription, RenewalState expected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(expected);
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        var updated = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE subscriptions.subscriptions
            SET status = {subscription.Status.ToName()}, period = {subscription.Period}, period_end = {subscription.PeriodEnd},
                failed_attempts = {subscription.FailedAttempts}, cancel_reason = {subscription.CancelReason}, updated_at = {subscription.UpdatedAt}
            WHERE tenant_id = {tenantId} AND id = {subscription.Id}
              AND status = {expected.Status.ToName()} AND period = {expected.Period} AND failed_attempts = {expected.FailedAttempts}
              AND cancel_at_period_end = {expected.CancelAtPeriodEnd}
            """,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated == 1;
    }
}
