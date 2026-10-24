using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using TenantFlow.Api.Endpoints.V1.TenantAccess;
using TenantFlow.Api.Endpoints.V1.Tenants.Orders.Schemas;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.Orders.Application.Orders.Dtos;
using TenantFlow.Orders.Application.Orders.Services;
using TenantFlow.Orders.Domain.Orders;

namespace TenantFlow.Api.Endpoints.V1.Tenants.Orders;

/// <summary>
/// The tenant's order routes (SPEC-0004). Every route requires <c>orders.place</c>; which order a caller may see or change
/// is decided per order by <see cref="OrderService"/> (owner, or <c>orders.manage</c> to read and cancel), and an order the
/// caller may not see is 404. Failures (ADR-0016): 400 invalid lines or missing <c>If-Match</c>; 404
/// <c>orders.order_not_found</c> or <c>catalog.product_not_found</c>; 409 <c>orders.invalid_transition</c>,
/// <c>orders.product_unavailable</c>, <c>orders.mixed_currency</c> or <c>orders.total_too_large</c>; 412
/// <c>orders.version_conflict</c>.
/// </summary>
internal static class OrderEndpoints
{
    // The Location header of a created order is generated from this route name.
    private const string GetOrderRoute = "GetOrder";

    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var orders = endpoints.MapGroup("/tenants/{tenantId}/orders")
            .WithTags("Orders");

        orders.MapPost("/", static async (OrderLinesRequest request, HttpContext http, ITenantContext tenant, LinkGenerator links, OrderService service, CancellationToken cancellationToken) =>
            {
                var order = await service.CreateAsync(request.ToCommand(), cancellationToken);
                http.Response.Headers.ETag = RowVersionETag.Format(order.Version);
                // A relative Location from the named route (not CreatedAtRoute, which builds an absolute URL from the Host header).
                var location = links.GetPathByName(http, GetOrderRoute, new { tenantId = tenant.TenantId, orderId = order.Id });
                return TypedResults.Created(location, OrderResponse.From(order));
            })
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireTenantPermission(TenantPermissions.OrdersPlace);

        orders.MapGet("/{orderId}", static async (string orderId, HttpContext http, OrderService service, CancellationToken cancellationToken) =>
                Respond(http, await service.GetAsync(OrderRouteId.Parse(orderId), cancellationToken)))
            .WithName(GetOrderRoute)
            .RequireTenantPermission(TenantPermissions.OrdersPlace);

        orders.MapPut("/{orderId}/lines", static async (string orderId, OrderLinesRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, HttpContext http, OrderService service, CancellationToken cancellationToken) =>
                Respond(http, await service.ReplaceLinesAsync(
                    OrderRouteId.Parse(orderId),
                    request.ToCommand(),
                    RowVersionETag.Parse(ifMatch, OrderErrors.IfMatchRequired),
                    cancellationToken)))
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .RequireTenantPermission(TenantPermissions.OrdersPlace);

        // A rejection for lack of stock is a business outcome: 200 with status "rejected", not an error.
        orders.MapPost("/{orderId}/submit", static async (string orderId, HttpContext http, OrderService service, CancellationToken cancellationToken) =>
                Respond(http, await service.SubmitAsync(OrderRouteId.Parse(orderId), cancellationToken)))
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireTenantPermission(TenantPermissions.OrdersPlace);

        // A paid order is cancelled by refunding it (SPEC-0007 criterion 14): orders.manage only, with a reason; 202 while
        // the refund runs. The body is optional for unpaid orders.
        orders.MapPost("/{orderId}/cancel", static async (string orderId, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CancelOrderRequest? request, HttpContext http, OrderService service, CancellationToken cancellationToken) =>
            {
                var order = await service.CancelAsync(OrderRouteId.Parse(orderId), request?.Reason, cancellationToken);
                var response = Respond(http, order);
                return order.Status == OrderStatusNames.CancellationPending
                    ? (IResult)TypedResults.Accepted((string?)null, response.Value)
                    : response;
            })
            .Produces<OrderResponse>(StatusCodes.Status200OK)
            .Produces<OrderResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireTenantPermission(TenantPermissions.OrdersPlace);

        return endpoints;
    }

    // Every order response carries its row version as the ETag, for the next If-Match.
    private static Ok<OrderResponse> Respond(HttpContext http, OrderDetails order)
    {
        http.Response.Headers.ETag = RowVersionETag.Format(order.Version);
        return TypedResults.Ok(OrderResponse.From(order));
    }
}
