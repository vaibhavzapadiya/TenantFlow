using TenantFlow.Payments.Application.Payments.Services;

namespace TenantFlow.Api.Endpoints.V1.Payments;

/// <summary>
/// Provider webhooks (SPEC-0006 criteria 7 to 10). Anonymous on purpose: the provider holds no TenantFlow token, so the
/// signature over the exact body bytes is the only authentication, and nothing is read or written before it verifies. The
/// body is read as raw bytes, never decoded first (the API's 64 KiB request limit applies). 200 for an applied, ignored or duplicate event, so the
/// provider stops retrying; 401 <c>payments.webhook.signature_invalid</c> or
/// <c>payments.webhook.timestamp_out_of_tolerance</c>; 400 <c>payments.webhook.malformed</c>; 404
/// <c>payments.webhook.provider_unknown</c> or <c>payments.webhook.payment_not_found</c>.
/// </summary>
internal static class PaymentWebhookEndpoints
{
    public static IEndpointRouteBuilder MapPaymentWebhookEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/payment-webhooks/{provider}", static async (string provider, HttpRequest request, PaymentWebhookService service, CancellationToken cancellationToken) =>
            {
                using var buffer = new MemoryStream();
                await request.Body.CopyToAsync(buffer, cancellationToken);
                var body = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
                var header = service.SignatureHeaderOf(provider);
                var signature = header is null ? null : request.Headers[header].ToString();
                await service.HandleAsync(provider, signature, body, cancellationToken);
                return TypedResults.Ok();
            })
            .AllowAnonymous()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithTags("Payment webhooks");

        return endpoints;
    }
}
