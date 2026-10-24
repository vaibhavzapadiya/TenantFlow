namespace TenantFlow.Payments.Infrastructure.Persistence;

/// <summary>
/// A webhook event that was received (ADR-0022 decision 5): its key is the provider and the provider's event id, written
/// in the same transaction as its effect, so a duplicate delivery changes nothing. Kept in M6; retention comes later.
/// </summary>
internal sealed class WebhookEventRow
{
    public string Provider { get; set; } = string.Empty;

    public string EventId { get; set; } = string.Empty;

    public Guid TenantId { get; set; }

    public Guid PaymentId { get; set; }

    /// <summary><c>applied</c> or <c>ignored</c> (the state table did not allow it).</summary>
    public string Outcome { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; set; }
}
