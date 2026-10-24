using System.Text.Json;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.Inventory.Contracts;
using TenantFlow.Orders.Application.Abstractions;
using TenantFlow.Orders.Domain.Orders;
using TenantFlow.Payments.Contracts.Events;
using TenantFlow.Tenants.Contracts;

namespace TenantFlow.Orders.Application.Orders.Services;

/// <summary>
/// The order payment saga (SPEC-0006 criterion 11, ADR-0022 decision 6): a process manager in Orders that moves an order
/// by its payment's events, through the order's state table. Its persisted state is the order row itself. It acts in the
/// message's tenant only (the Worker enters it from the envelope; it is checked to exist here), each message has one
/// effect, and an event the order's state does not allow is acknowledged and ignored. A failed payment releases the
/// order's stock after the state change commits, and again on any redelivery, so a crash in between still releases it.
/// </summary>
public sealed class PaymentEventConsumer(IOrderStore store, IStockReservations reservations, ITenantDirectory tenants, ITenantContext context)
{
    /// <summary>This consumer's name in <c>processed_messages</c>, and its queue.</summary>
    public const string Consumer = "orders.payment-events";

    /// <summary>The event types this consumer subscribes to.</summary>
    public static readonly IReadOnlyList<string> EventTypes =
        [PaymentSucceededV1.EventType, PaymentFailedV1.EventType, PaymentReviewRequiredV1.EventType, RefundSucceededV1.EventType, RefundFailedV1.EventType];

    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    /// <exception cref="InvalidMessageException">Unknown type, version, tenant or order, or an unreadable payload.</exception>
    public async Task<MessageOutcome> HandleAsync(IncomingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var (trigger, orderId) = message.Type switch
        {
            _ when message.Type == PaymentSucceededV1.EventType => (OrderTrigger.PaymentSucceeded, Read<PaymentSucceededV1>(message, PaymentSucceededV1.EventVersion).OrderId),
            _ when message.Type == PaymentFailedV1.EventType => (OrderTrigger.PaymentFailed, Read<PaymentFailedV1>(message, PaymentFailedV1.EventVersion).OrderId),
            _ when message.Type == PaymentReviewRequiredV1.EventType => (OrderTrigger.PaymentReviewRequired, Read<PaymentReviewRequiredV1>(message, PaymentReviewRequiredV1.EventVersion).OrderId),
            _ when message.Type == RefundSucceededV1.EventType => RefundSucceeded(Read<RefundSucceededV1>(message, RefundSucceededV1.EventVersion)),
            _ when message.Type == RefundFailedV1.EventType => RefundFailed(Read<RefundFailedV1>(message, RefundFailedV1.EventVersion)),
            _ => throw new InvalidMessageException("Unknown message type."),
        };
        if (orderId == Guid.Empty)
        {
            throw new InvalidMessageException("Unreadable payload.");
        }

        if (context.TenantId != message.TenantId)
        {
            // A host error, never a message error: the scope must act for exactly the message's tenant.
            throw new InvalidOperationException("The tenant context does not match the message's tenant.");
        }

        var known = await tenants.GetAvailabilityAsync([message.TenantId], cancellationToken);
        if (!known.ContainsKey(message.TenantId))
        {
            throw new InvalidMessageException("Unknown tenant.");
        }

        if (trigger is OrderTrigger.RefundedPartly or OrderTrigger.RefundedFully
            && (await store.FindAsync(orderId, cancellationToken))?.Order.Status is OrderStatus.PendingPayment or OrderStatus.PaymentReview)
        {
            // A refund cannot come before its payment: the payment event is still on its way (redelivered or reordered).
            // Not recorded, so the broker delivers it again later (review fix R6).
            throw new InvalidOperationException("The refunded order is not paid yet; the message is retried.");
        }

        var effect = await store.ApplyMessageAsync(orderId, trigger, Consumer, message.MessageId, cancellationToken);
        if (effect.Kind == MessageEffectKind.OrderMissing)
        {
            throw new InvalidMessageException("Unknown order.");
        }

        if (effect.Status == OrderStatus.PaymentFailed)
        {
            // Committed as failed first, then released (idempotent): a redelivery after a crash here releases it.
            await reservations.ReleaseAsync(orderId, cancellationToken);
        }

        return effect.Kind == MessageEffectKind.Duplicate ? MessageOutcome.Duplicate : MessageOutcome.Processed;
    }

    // SPEC-0007 criterion 13: a partial refund keeps the order paid in part, a full one refunds it.
    private static (OrderTrigger, Guid) RefundSucceeded(RefundSucceededV1 refund) =>
        (refund.FullyRefunded ? OrderTrigger.RefundedFully : OrderTrigger.RefundedPartly, refund.OrderId);

    // SPEC-0007 criterion 14: a failed cancellation refund returns the order to what it was; any other failed refund changes
    // nothing (its trigger is in no row of the state table, so the message is recorded and ignored).
    private static (OrderTrigger, Guid) RefundFailed(RefundFailedV1 refund) =>
        (!refund.Cancellation ? OrderTrigger.RefundFailed
            : refund.RefundedTotal > 0 ? OrderTrigger.CancellationRefundFailedAfterRefunds
            : OrderTrigger.CancellationRefundFailed, refund.OrderId);

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
