using TenantFlow.Notifications.Domain.Notifications;

namespace TenantFlow.Notifications.Application.Abstractions;

/// <summary>
/// Storage for notifications. Unlike request-serving stores it takes the tenant explicitly: a consumer has no request
/// and no membership, so its tenant comes from the validated message envelope (ADR-0010 service boundary, ADR-0021).
/// Every write runs in a transaction that sets that tenant for row-level security.
/// </summary>
public interface INotificationStore
{
    /// <summary>
    /// Records the notification and the processed message <paramref name="messageId"/> for <paramref name="consumer"/> in
    /// one transaction. False, with nothing written, when that consumer already processed the message.
    /// </summary>
    Task<bool> RecordAsync(Guid tenantId, Notification notification, string consumer, Guid messageId, CancellationToken cancellationToken);
}
