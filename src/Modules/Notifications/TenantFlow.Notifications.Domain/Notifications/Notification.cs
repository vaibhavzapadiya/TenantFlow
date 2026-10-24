namespace TenantFlow.Notifications.Domain.Notifications;

/// <summary>
/// A notification recorded for a tenant member because of an integration event (SPEC-0005). M5 records it only; delivery,
/// templates and preferences come later. It holds identifiers, never personal data.
/// </summary>
public sealed class Notification
{
    private Notification(Guid id, Guid tenantId, string kind, Guid orderId, Guid recipientMembershipId, Guid messageId, DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        Kind = kind;
        OrderId = orderId;
        RecipientMembershipId = recipientMembershipId;
        MessageId = messageId;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>A stable name from <see cref="NotificationKinds"/>.</summary>
    public string Kind { get; private set; }

    public Guid OrderId { get; private set; }

    public Guid RecipientMembershipId { get; private set; }

    /// <summary>The message that caused it, for tracing a notification back to its event.</summary>
    public Guid MessageId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <exception cref="ArgumentException">An empty id or an unknown kind: a programming error.</exception>
    public static Notification Create(Guid id, Guid tenantId, string kind, Guid orderId, Guid recipientMembershipId, Guid messageId, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || orderId == Guid.Empty || recipientMembershipId == Guid.Empty || messageId == Guid.Empty)
        {
            throw new ArgumentException("A notification needs its ids.");
        }

        if (!NotificationKinds.All.Contains(kind))
        {
            throw new ArgumentException("Unknown notification kind.", nameof(kind));
        }

        return new Notification(id, tenantId, kind, orderId, recipientMembershipId, messageId, createdAt);
    }
}
