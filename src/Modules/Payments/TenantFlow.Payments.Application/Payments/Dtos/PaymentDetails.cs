using TenantFlow.Payments.Domain.Payments;

namespace TenantFlow.Payments.Application.Payments.Dtos;

/// <summary>
/// A payment as callers see it. Its <see cref="Status"/> is the public name: an unknown outcome reads <c>processing</c>,
/// because to the customer it is still being settled (SPEC-0006 criterion 5). This is also what an idempotency key replays.
/// </summary>
public sealed record PaymentDetails(Guid Id, Guid? OrderId, string Status, decimal Amount, string Currency, DateTimeOffset CreatedAt)
{
    public static PaymentDetails From(Payment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);
        var status = payment.Status == PaymentStatus.Unknown ? PaymentStatusNames.Processing : payment.Status.ToName();
        return new PaymentDetails(payment.Id, payment.OrderId, status, payment.Amount, payment.Currency, payment.CreatedAt);
    }
}
