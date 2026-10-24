using FluentValidation;
using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.Subscriptions.Application.Abstractions;
using TenantFlow.Subscriptions.Application.Subscriptions.Commands;
using TenantFlow.Subscriptions.Application.Subscriptions.Dtos;
using TenantFlow.Subscriptions.Domain.Subscriptions;

namespace TenantFlow.Subscriptions.Application.Subscriptions.Services;

/// <summary>
/// Subscribing, reading and cancelling (SPEC-0007 criteria 2 and 3). The routes check <c>subscriptions.subscribe</c>; a
/// subscription is visible only to its subscriber and to <c>subscriptions.manage</c> holders, and to anyone else it does
/// not exist. Subscribing calls no provider: the first charge is due work of the Worker (ADR-0023).
/// </summary>
public sealed class SubscriptionService(ITenantContext context, ISubscriptionStore store, IValidator<SubscribeCommand> validator, TimeProvider clock)
{
    // A cancel that loses to a concurrent renewal reads the new state and tries again; more losses than this are a bug.
    private const int CancelAttempts = 3;

    /// <exception cref="AppException">
    /// <see cref="SubscriptionErrors.PlanNotFound"/> or <see cref="SubscriptionErrors.AlreadySubscribed"/>.
    /// </exception>
    public async Task<SubscriptionDetails> SubscribeAsync(SubscribeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var plan = await store.FindPlanAsync(command.PlanId, cancellationToken);
        if (plan is not { Active: true })
        {
            throw new AppException(SubscriptionErrors.PlanNotFound);
        }

        var now = clock.GetUtcNow();
        var subscription = Subscription.Start(Guid.CreateVersion7(now), plan, context.MembershipId, command.PaymentMethod, now);
        // The natural key decides a repeat (SPEC-0007 decision 2): one live subscription per member and plan.
        return await store.TryStartAsync(subscription, cancellationToken)
            ? SubscriptionDetails.From(subscription)
            : throw new AppException(SubscriptionErrors.AlreadySubscribed);
    }

    /// <exception cref="AppException"><see cref="SubscriptionErrors.NotFound"/>.</exception>
    public async Task<SubscriptionDetails> GetAsync(Guid subscriptionId, CancellationToken cancellationToken) =>
        SubscriptionDetails.From(await FindVisibleAsync(subscriptionId, cancellationToken));

    /// <exception cref="AppException">
    /// <see cref="SubscriptionErrors.NotFound"/> or <see cref="SubscriptionErrors.InvalidTransition"/> (already cancelled).
    /// </exception>
    public async Task<SubscriptionDetails> CancelAsync(Guid subscriptionId, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var subscription = await FindVisibleAsync(subscriptionId, cancellationToken);
            if (subscription.CancelAtPeriodEnd)
            {
                // Already cancelled at the period end: asking again is answered, not refused.
                return SubscriptionDetails.From(subscription);
            }

            var expected = subscription.Status;
            subscription.Cancel(clock.GetUtcNow());
            if (await store.SaveCancellationAsync(subscription, expected, cancellationToken))
            {
                return SubscriptionDetails.From(subscription);
            }

            if (attempt == CancelAttempts)
            {
                throw new InvalidOperationException("The subscription kept changing while it was being cancelled.");
            }
        }
    }

    private async Task<Subscription> FindVisibleAsync(Guid subscriptionId, CancellationToken cancellationToken)
    {
        var subscription = await store.FindAsync(subscriptionId, cancellationToken);
        return subscription is not null && (subscription.IsOwnedBy(context.MembershipId) || context.HasPermission(TenantPermissions.SubscriptionsManage))
            ? subscription
            : throw new AppException(SubscriptionErrors.NotFound);
    }
}
