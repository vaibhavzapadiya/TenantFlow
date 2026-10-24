using System.Text.Json;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.Notifications.Application.Abstractions;
using TenantFlow.Notifications.Domain.Notifications;
using TenantFlow.Orders.Contracts.Events;
using TenantFlow.Tenants.Contracts;

namespace TenantFlow.Notifications.Application.Notifications.Services;

/// <summary>
/// Records a notification for the order's owner when an order is reserved or cancelled (SPEC-0005 criteria 9 to 11). It
/// acts in the message's tenant only, and only after checking that the tenant exists; a message it can never process
/// throws <see cref="InvalidMessageException"/>. Repeats of a message id are recognised and change nothing.
/// </summary>
public sealed class OrderEventConsumer(INotificationStore store, ITenantDirectory tenants, TimeProvider clock)
{
    /// <summary>This consumer's name in <c>processed_messages</c>, and its queue.</summary>
    public const string Consumer = "notifications.order-events";

    /// <summary>The event types this consumer subscribes to.</summary>
    public static readonly IReadOnlyList<string> EventTypes = [OrderReservedV1.EventType, OrderCancelledV1.EventType];

    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    /// <exception cref="InvalidMessageException">Unknown type, version or tenant, or an unreadable payload.</exception>
    public async Task<MessageOutcome> HandleAsync(IncomingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var (kind, orderId, recipient) = message.Type switch
        {
            _ when message.Type == OrderReservedV1.EventType => Reserved(Read<OrderReservedV1>(message, OrderReservedV1.EventVersion)),
            _ when message.Type == OrderCancelledV1.EventType => Cancelled(Read<OrderCancelledV1>(message, OrderCancelledV1.EventVersion)),
            _ => throw new InvalidMessageException("Unknown message type."),
        };
        if (orderId == Guid.Empty || recipient == Guid.Empty)
        {
            throw new InvalidMessageException("Unreadable payload.");
        }

        // The envelope's tenant is well formed; it must also exist before anything is written for it.
        var known = await tenants.GetAvailabilityAsync([message.TenantId], cancellationToken);
        if (!known.ContainsKey(message.TenantId))
        {
            throw new InvalidMessageException("Unknown tenant.");
        }

        var now = clock.GetUtcNow();
        var notification = Notification.Create(Guid.CreateVersion7(now), message.TenantId, kind, orderId, recipient, message.MessageId, now);
        return await store.RecordAsync(message.TenantId, notification, Consumer, message.MessageId, cancellationToken)
            ? MessageOutcome.Processed
            : MessageOutcome.Duplicate;
    }

    private static (string Kind, Guid OrderId, Guid Recipient) Reserved(OrderReservedV1 e) => (NotificationKinds.OrderReserved, e.OrderId, e.OwnerMembershipId);

    private static (string Kind, Guid OrderId, Guid Recipient) Cancelled(OrderCancelledV1 e) => (NotificationKinds.OrderCancelled, e.OrderId, e.OwnerMembershipId);

    private static TEvent Read<TEvent>(IncomingMessage message, int version)
        where TEvent : class
    {
        if (message.Version != version)
        {
            throw new InvalidMessageException("Unsupported message version.");
        }

        TEvent? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TEvent>(message.Payload, PayloadOptions);
        }
        catch (JsonException exception)
        {
            // The parser's message can quote payload text; the reason stays generic.
            throw new InvalidMessageException("Unreadable payload.", exception);
        }

        return payload ?? throw new InvalidMessageException("Unreadable payload.");
    }
}
