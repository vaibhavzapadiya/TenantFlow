using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.Orders.Domain.Orders;

namespace TenantFlow.Orders.Application.Abstractions;

/// <summary>
/// Storage for the Orders use cases. Every method acts on the tenant in the current tenant context: it takes no tenant
/// id, filters by it, and runs in a transaction that sets it for row-level security (ADR-0011). With no tenant set it
/// throws before touching data.
/// </summary>
public interface IOrderStore
{
    Task<OrderRecord> AddAsync(Order order, CancellationToken cancellationToken);

    /// <summary>
    /// The order with its lines as the database holds it now, tracked for a later <see cref="SaveAsync"/> in the same
    /// scope. Each call reads afresh, so it sees a transition made by <see cref="TryTransitionAsync"/> or another request.
    /// </summary>
    Task<OrderRecord?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Saves a changed order only if its row version is still <paramref name="expectedVersion"/>.</summary>
    /// <exception cref="TenantFlow.BuildingBlocks.Abstractions.Errors.AppException"><see cref="OrderErrors.VersionConflict"/>.</exception>
    Task<OrderRecord> SaveAsync(Order order, uint expectedVersion, CancellationToken cancellationToken);

    /// <summary>
    /// Moves the order from <paramref name="expected"/> to <paramref name="target"/> with one conditional update, so of two racing
    /// commands exactly one wins (ADR-0020). With <paramref name="expectedVersion"/>, the row must also still be that version,
    /// so the caller acts on exactly the order it read. False when the order was no longer as expected.
    /// A <paramref name="message"/> is written to the outbox in the same transaction, and only when the order moved (ADR-0021).
    /// A move to <see cref="OrderStatus.Cancelled"/> also requires that no payment has begun (SPEC-0006 criterion 6). A move
    /// to <c>pending_reservation</c> or <c>pending_payment</c> schedules its deadline (<c>OrderDeadlines</c>) in the same
    /// transaction (ADR-0022 decision 7).
    /// </summary>
    Task<bool> TryTransitionAsync(Guid id, OrderStatus expected, OrderStatus target, string? rejectionReason, uint? expectedVersion, OutgoingMessage? message, CancellationToken cancellationToken);

    /// <summary>
    /// Marks a payment begun on a <see cref="OrderStatus.PendingPayment"/> order that has none yet, with one conditional
    /// update, so it races safely with cancel (ADR-0022 decision 3). False when the order was not in that state or a
    /// payment had already begun.
    /// </summary>
    Task<bool> TryBeginPaymentAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a paid order from <paramref name="expected"/> to <c>cancellation_pending</c> with one conditional update, records
    /// who cancelled and why, and schedules the cancellation refund job and writes <paramref name="audit"/> to the outbox in
    /// the same transaction (SPEC-0007 criterion 14, SPEC-0009 criterion 2). False when the order was no longer as expected.
    /// </summary>
    Task<bool> TryRequestCancellationAsync(Guid id, OrderStatus expected, string reason, Guid requestedBy, OutgoingMessage audit, CancellationToken cancellationToken);

    /// <summary>
    /// For a consumed message (ADR-0021 decision 5), in one transaction: records <paramref name="messageId"/> for
    /// <paramref name="consumer"/>, then moves the order by <paramref name="trigger"/> if the state table allows it. A
    /// repeated message id changes nothing. The answer says what happened and the order's status afterwards.
    /// </summary>
    Task<MessageEffect> ApplyMessageAsync(Guid id, OrderTrigger trigger, string consumer, Guid messageId, CancellationToken cancellationToken);
}
