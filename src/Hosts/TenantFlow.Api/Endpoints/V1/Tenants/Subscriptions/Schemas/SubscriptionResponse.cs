using TenantFlow.Api.Endpoints.V1.Tenants.Orders.Schemas;
using TenantFlow.Subscriptions.Application.Subscriptions.Dtos;

namespace TenantFlow.Api.Endpoints.V1.Tenants.Subscriptions.Schemas;

/// <summary>
/// A subscription as the API returns it. Status is <c>trialing</c>, <c>incomplete</c>, <c>active</c>, <c>past_due</c> or
/// <c>cancelled</c>; the next charge is due at <see cref="CurrentPeriodEnd"/> unless <see cref="CancelAtPeriodEnd"/>.
/// </summary>
internal sealed record SubscriptionResponse(Guid Id, Guid PlanId, string Status, MoneyResponse Price, string Interval, DateTimeOffset CurrentPeriodEnd, bool CancelAtPeriodEnd, DateTimeOffset CreatedAt)
{
    public static SubscriptionResponse From(SubscriptionDetails subscription) =>
        new(subscription.Id, subscription.PlanId, subscription.Status, new MoneyResponse(subscription.Amount, subscription.Currency), subscription.Interval,
            subscription.CurrentPeriodEnd, subscription.CancelAtPeriodEnd, subscription.CreatedAt);
}
