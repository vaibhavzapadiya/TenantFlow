using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.Subscriptions.Domain.Subscriptions;

namespace TenantFlow.Api.Endpoints.V1.Tenants.Subscriptions;

/// <summary>Reads a subscription id from a route: only the canonical form can name one, anything else is an unknown subscription.</summary>
internal static class SubscriptionRouteId
{
    /// <exception cref="AppException"><see cref="SubscriptionErrors.NotFound"/> when the value is not a subscription id.</exception>
    public static Guid Parse(string value) =>
        RouteId.Parse(value, SubscriptionErrors.NotFound);
}
