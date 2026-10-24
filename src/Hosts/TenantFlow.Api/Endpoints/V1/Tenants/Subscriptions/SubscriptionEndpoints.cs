using TenantFlow.Api.Endpoints.V1.TenantAccess;
using TenantFlow.Api.Endpoints.V1.Tenants.Subscriptions.Schemas;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.Subscriptions.Application.Subscriptions.Commands;
using TenantFlow.Subscriptions.Application.Subscriptions.Services;

namespace TenantFlow.Api.Endpoints.V1.Tenants.Subscriptions;

/// <summary>
/// Subscribing, reading and cancelling (SPEC-0007 criteria 2 and 3). Every route requires <c>subscriptions.subscribe</c>;
/// a subscription is visible to its subscriber and to <c>subscriptions.manage</c> holders, and 404 to anyone else.
/// Subscribing charges nothing in the request: the first charge is due work of the Worker. Failures (ADR-0016): 400
/// invalid payment method; 404 <c>subscriptions.plan_not_found</c> or <c>subscriptions.subscription_not_found</c>; 409
/// <c>subscriptions.already_subscribed</c> or <c>subscriptions.invalid_transition</c>.
/// </summary>
internal static class SubscriptionEndpoints
{
    // The Location header of a new subscription is generated from this route name.
    private const string GetSubscriptionRoute = "GetSubscription";

    public static IEndpointRouteBuilder MapSubscriptionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var subscriptions = endpoints.MapGroup("/tenants/{tenantId}/subscriptions")
            .WithTags("Subscriptions");

        subscriptions.MapPost("/", static async (SubscribeRequest request, HttpContext http, ITenantContext tenant, LinkGenerator links, SubscriptionService service, CancellationToken cancellationToken) =>
            {
                var subscription = await service.SubscribeAsync(new SubscribeCommand(request.PlanId ?? Guid.Empty, request.PaymentMethod), cancellationToken);
                // A relative Location from the named route (not CreatedAtRoute, which builds an absolute URL from the Host header).
                var location = links.GetPathByName(http, GetSubscriptionRoute, new { tenantId = tenant.TenantId, subscriptionId = subscription.Id });
                return TypedResults.Created(location, SubscriptionResponse.From(subscription));
            })
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireTenantPermission(TenantPermissions.SubscriptionsSubscribe);

        subscriptions.MapGet("/{subscriptionId}", static async (string subscriptionId, SubscriptionService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(SubscriptionResponse.From(await service.GetAsync(SubscriptionRouteId.Parse(subscriptionId), cancellationToken))))
            .WithName(GetSubscriptionRoute)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireTenantPermission(TenantPermissions.SubscriptionsSubscribe);

        subscriptions.MapPost("/{subscriptionId}/cancel", static async (string subscriptionId, SubscriptionService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(SubscriptionResponse.From(await service.CancelAsync(SubscriptionRouteId.Parse(subscriptionId), cancellationToken))))
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireTenantPermission(TenantPermissions.SubscriptionsSubscribe);

        return endpoints;
    }
}
