using Microsoft.AspNetCore.Mvc;
using TenantFlow.Api.Endpoints.V1.TenantAccess;
using TenantFlow.Api.Endpoints.V1.Tenants.Orders;
using TenantFlow.Api.Endpoints.V1.Tenants.Payments.Schemas;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.Payments.Application.Refunds.Commands;
using TenantFlow.Payments.Application.Refunds.Services;

namespace TenantFlow.Api.Endpoints.V1.Tenants.Payments;

/// <summary>
/// Refunding an order's payment (SPEC-0007 criteria 9 to 11). Both routes require <c>orders.manage</c>. Creating requires
/// an <c>Idempotency-Key</c> header, an amount in the payment's currency and a reason; a repeat with the same key and body
/// replays the first response. A declined refund is a business outcome: 201 with status <c>failed</c>. Failures
/// (ADR-0016): 400 invalid key, amount or reason; 404 <c>payments.payment_not_found</c>; 409 <c>payments.not_refundable</c>,
/// <c>payments.refund_exceeds_captured</c> or <c>payments.request_in_progress</c>; 422 <c>payments.idempotency_key_reused</c>.
/// </summary>
internal static class RefundEndpoints
{
    public static IEndpointRouteBuilder MapRefundEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var refunds = endpoints.MapGroup("/tenants/{tenantId}/orders/{orderId}/payments/current/refunds")
            .WithTags("Refunds");

        refunds.MapPost("/", static async (string orderId, CreateRefundRequest request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, RefundService service, CancellationToken cancellationToken) =>
            {
                var refund = await service.CreateAsync(new CreateRefundCommand(OrderRouteId.Parse(orderId), idempotencyKey, request.Amount, request.Reason), cancellationToken);
                return TypedResults.Created((string?)null, RefundResponse.From(refund));
            })
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireTenantPermission(TenantPermissions.OrdersManage);

        refunds.MapGet("/", static async (string orderId, RefundService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(new RefundListResponse([.. (await service.ListAsync(OrderRouteId.Parse(orderId), cancellationToken)).Select(RefundResponse.From)])))
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireTenantPermission(TenantPermissions.OrdersManage);

        return endpoints;
    }
}
