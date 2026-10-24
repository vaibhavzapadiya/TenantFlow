using TenantFlow.Api.Endpoints.V1.Tenants.Orders.Schemas;
using TenantFlow.Payments.Application.Payments.Dtos;

namespace TenantFlow.Api.Endpoints.V1.Tenants.Payments.Schemas;

/// <summary>A payment as the API returns it. Status is <c>processing</c>, <c>succeeded</c>, <c>failed</c> or <c>requires_review</c>.</summary>
internal sealed record PaymentResponse(Guid Id, Guid OrderId, string Status, MoneyResponse Amount, DateTimeOffset CreatedAt)
{
    public static PaymentResponse From(PaymentDetails payment) =>
        new(payment.Id, payment.OrderId ?? throw new InvalidOperationException("Only order payments are served by this API."), payment.Status, new MoneyResponse(payment.Amount, payment.Currency), payment.CreatedAt);
}
