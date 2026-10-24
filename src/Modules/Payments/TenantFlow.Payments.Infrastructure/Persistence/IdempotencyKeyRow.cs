namespace TenantFlow.Payments.Infrastructure.Persistence;

/// <summary>
/// An idempotency key of a tenant (ADR-0022 decision 4): the hash of the request it was first used with, the payment it
/// started, and, once the request finished, its response. Kept in M6; cleanup comes with a measured retention.
/// </summary>
internal sealed class IdempotencyKeyRow
{
    public Guid TenantId { get; set; }

    public string Operation { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    /// <summary>Lower-case hex SHA-256 of the canonical request.</summary>
    public string RequestHash { get; set; } = string.Empty;

    public Guid PaymentId { get; set; }

    /// <summary>The refund the request started, for a <c>create_refund</c> key (SPEC-0007 criterion 11); null for a payment.</summary>
    public Guid? RefundId { get; set; }

    /// <summary>The stored response body (a <c>PaymentDetails</c> or <c>RefundDetails</c> as JSON); null while the request runs.</summary>
    public string? ResponseBody { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}
