using Microsoft.AspNetCore.Mvc;
using TenantFlow.Api.Endpoints.V1.TenantAccess;
using TenantFlow.Api.Endpoints.V1.Tenants.Orders;
using TenantFlow.Api.Endpoints.V1.Tenants.Payments.Schemas;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.Payments.Application.Payments.Commands;
using TenantFlow.Payments.Application.Payments.Services;

namespace TenantFlow.Api.Endpoints.V1.Tenants.Payments;

/// <summary>
/// Paying an order (SPEC-0006 criteria 1 to 6). Both routes require <c>orders.place</c>; only the order's owner may pay it,
/// and its owner or an <c>orders.manage</c> holder may read the payment; to anyone else it is 404. Creating requires an
/// <c>Idempotency-Key</c> header: a repeat with the same key and body replays the first response. A decline is a business
/// outcome: 201 with status <c>failed</c>, not an error. Failures (ADR-0016): 400 invalid key or payment method; 404
/// <c>orders.order_not_found</c> or <c>payments.payment_not_found</c>; 409 <c>orders.invalid_transition</c>,
/// <c>payments.already_exists</c> or <c>payments.request_in_progress</c>; 422 <c>payments.idempotency_key_reused</c>.
/// </summary>
internal static class PaymentEndpoints
{
    // The Location header of a created payment is generated from this route name.
    private const string GetPaymentRoute = "GetOrderPayment";

    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var payments = endpoints.MapGroup("/tenants/{tenantId}/orders/{orderId}/payments")
            .WithTags("Payments");

        payments.MapPost("/", static async (string orderId, CreatePaymentRequest request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, HttpContext http, ITenantContext tenant, LinkGenerator links, PaymentService service, CancellationToken cancellationToken) =>
            {
                var id = OrderRouteId.Parse(orderId);
                var payment = await service.CreateAsync(new CreatePaymentCommand(id, idempotencyKey, request.PaymentMethod), cancellationToken);
                // A relative Location from the named route (not CreatedAtRoute, which builds an absolute URL from the Host header).
                var location = links.GetPathByName(http, GetPaymentRoute, new { tenantId = tenant.TenantId, orderId = id });
                return TypedResults.Created(location, PaymentResponse.From(payment));
            })
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireTenantPermission(TenantPermissions.OrdersPlace);

        payments.MapGet("/current", static async (string orderId, PaymentService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(PaymentResponse.From(await service.GetForOrderAsync(OrderRouteId.Parse(orderId), cancellationToken))))
            .WithName(GetPaymentRoute)
            .RequireTenantPermission(TenantPermissions.OrdersPlace);

        return endpoints;
    }
}
