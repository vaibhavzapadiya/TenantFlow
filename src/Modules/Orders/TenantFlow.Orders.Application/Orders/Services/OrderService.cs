using FluentValidation;
using TenantFlow.Audit.Contracts;
using TenantFlow.Audit.Contracts.Events;
using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.Catalog.Contracts;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.Inventory.Contracts;
using TenantFlow.Orders.Application.Abstractions;
using TenantFlow.Orders.Application.Orders.Commands;
using TenantFlow.Orders.Application.Orders.Dtos;
using TenantFlow.Orders.Contracts.Events;
using TenantFlow.Orders.Domain.Orders;

namespace TenantFlow.Orders.Application.Orders.Services;

/// <summary>
/// The tenant-owned order use cases (SPEC-0004). Every one acts on the tenant in the <see cref="ITenantContext"/> and takes
/// no tenant id. The route has checked <c>orders.place</c>; what the caller may do to a given order is decided here: the
/// owner may read, change, submit and cancel it, a holder of <c>orders.manage</c> may read and cancel any order of the
/// tenant, and to anyone else the order does not exist (404, never 403, so ids reveal nothing).
/// <para>
/// Submit and cancel cross into Inventory through its contract, in Inventory's own transaction (ADR-0020): each step
/// commits before the next, nothing remote runs inside a transaction (AGENTS.md rule 3), every state change is a
/// conditional update, and both Inventory calls are idempotent, so repeating an interrupted command finishes it.
/// </para>
/// </summary>
public sealed class OrderService(
    ITenantContext context,
    IOrderStore store,
    IProductCatalog catalog,
    IStockReservations reservations,
    OrderReservationStep reservation,
    IValidator<OrderLinesCommand> validator,
    TimeProvider clock)
{
    // Each lost race moves the order forward through a state table with no cycle, so a handful of attempts is plenty.
    private const int CancelAttempts = 5;

    /// <exception cref="AppException">
    /// <see cref="CatalogContractErrors.ProductNotFound"/>, <see cref="OrderErrors.ProductUnavailable"/>,
    /// <see cref="OrderErrors.MixedCurrency"/>, or <see cref="OrderErrors.TotalTooLarge"/>.
    /// </exception>
    public async Task<OrderDetails> CreateAsync(OrderLinesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var lines = await SnapshotAsync(command, cancellationToken);
        var now = clock.GetUtcNow();
        var order = Order.Create(Guid.CreateVersion7(now), context.TenantId, context.MembershipId, lines, now);
        return OrderDetails.From(await store.AddAsync(order, cancellationToken));
    }

    /// <exception cref="AppException"><see cref="OrderErrors.NotFound"/>: unknown, another tenant's, or not the caller's to see.</exception>
    public async Task<OrderDetails> GetAsync(Guid id, CancellationToken cancellationToken) =>
        OrderDetails.From(await FindVisibleAsync(id, cancellationToken));

    /// <summary>
    /// Replaces a draft's lines if its current version is one of <paramref name="acceptedVersions"/> (the caller's
    /// <c>If-Match</c>); null accepts any current version (<c>If-Match: *</c>). Only the owner may change an order's lines.
    /// </summary>
    /// <exception cref="AppException">
    /// <see cref="OrderErrors.NotFound"/>, <see cref="OrderErrors.VersionConflict"/>, <see cref="OrderErrors.InvalidTransition"/>
    /// after the draft stage, or a product error as for <see cref="CreateAsync"/>.
    /// </exception>
    public async Task<OrderDetails> ReplaceLinesAsync(Guid id, OrderLinesCommand command, IReadOnlyCollection<uint>? acceptedVersions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var record = await FindOwnAsync(id, cancellationToken);
        if (acceptedVersions is not null && !acceptedVersions.Contains(record.Version))
        {
            throw new AppException(OrderErrors.VersionConflict);
        }

        // The state is checked before the catalog is asked, so a submitted order is a 409 whatever its new lines name.
        record.Order.NextOrThrow(OrderTrigger.EditLines);
        record.Order.ReplaceLines(await SnapshotAsync(command, cancellationToken), clock.GetUtcNow());
        // The store compares the version again at write time, so a change between the read and the write still loses.
        return OrderDetails.From(await store.SaveAsync(record.Order, record.Version, cancellationToken));
    }

    /// <summary>
    /// Submits a draft: <c>Draft</c> to <c>PendingReservation</c> (committed), then reserves every line in Inventory, then
    /// <c>PendingPayment</c> or, when the stock cannot cover it, <c>Rejected</c>. An order left in <c>PendingReservation</c>
    /// by an interruption is finished by submitting it again; its reservation is never taken twice.
    /// </summary>
    /// <exception cref="AppException"><see cref="OrderErrors.NotFound"/>, or <see cref="OrderErrors.InvalidTransition"/> past that stage.</exception>
    public async Task<OrderDetails> SubmitAsync(Guid id, CancellationToken cancellationToken)
    {
        var (order, version) = await FindOwnAsync(id, cancellationToken);
        var status = order.Status;
        if (status == OrderStatus.Draft)
        {
            // Committed before Inventory is called, and only if the order is still the version just read: its lines are then
            // exactly the lines read here (a concurrent line edit makes this update miss), and they cannot change again.
            // If another request moved the order first, continue from what it is now.
            var pending = order.NextOrThrow(OrderTrigger.Submit);
            if (await store.TryTransitionAsync(id, OrderStatus.Draft, pending, null, version, null, cancellationToken))
            {
                status = pending;
            }
            else
            {
                (order, _) = await FindOwnAsync(id, cancellationToken);
                status = order.Status;
            }
        }

        if (status != OrderStatus.PendingReservation)
        {
            throw new AppException(OrderErrors.InvalidTransition);
        }

        await reservation.FinishAsync(order, cancellationToken);
        return OrderDetails.From(await FindOwnAsync(id, cancellationToken));
    }

    /// <summary>
    /// Cancels a draft, or a reserved order and then releases its stock. Cancelling a cancelled order again releases
    /// anything still held (an interrupted cancel) and changes nothing else. The owner or a holder of <c>orders.manage</c>
    /// may cancel.
    /// </summary>
    /// <exception cref="AppException">
    /// <see cref="OrderErrors.NotFound"/>, <see cref="OrderErrors.InvalidTransition"/> while reserving or after a rejection, or
    /// <see cref="OrderErrors.PaymentInProgress"/> once a payment has begun (SPEC-0006 criterion 6).
    /// </exception>
    public Task<OrderDetails> CancelAsync(Guid id, string? reason, CancellationToken cancellationToken) =>
        CancelAsync(id, reason, attempt: 1, cancellationToken);

    // A lost race decides again on what the order is now, a bounded number of times, like SubscriptionService.
    private async Task<OrderDetails> CancelAsync(Guid id, string? reason, int attempt, CancellationToken cancellationToken)
    {
        if (attempt > CancelAttempts)
        {
            throw new InvalidOperationException("The order kept changing while it was being cancelled.");
        }

        var order = (await FindVisibleAsync(id, cancellationToken)).Order;
        if (order.Status is OrderStatus.Paid or OrderStatus.PartiallyRefunded or OrderStatus.CancellationPending)
        {
            return await CancelPaidAsync(order, reason, attempt, cancellationToken);
        }

        if (order.Status != OrderStatus.Cancelled)
        {
            // A state with no way to cancel says so, before anything about payments (review fix R8).
            order.NextOrThrow(OrderTrigger.Cancel);
            if (order.PaymentStartedAt is not null)
            {
                throw new AppException(OrderErrors.PaymentInProgress);
            }

            var from = order.Status;
            var cancelled = OutgoingMessage.From(new OrderCancelledV1(id, order.CustomerMembershipId, from.ToName()));
            if (!await store.TryTransitionAsync(id, from, order.NextOrThrow(OrderTrigger.Cancel), null, null, cancelled, cancellationToken))
            {
                // Moved by another request between the read and the write, or a payment began: decide again on what it
                // is now. The state table has no cycle and a payment never un-begins, so this repeats at most a few times.
                return await CancelAsync(id, reason, attempt + 1, cancellationToken);
            }
        }

        // Committed as cancelled first, then released: a crash in between leaves units held until cancel is repeated.
        await reservations.ReleaseAsync(id, cancellationToken);
        return OrderDetails.From(await FindVisibleAsync(id, cancellationToken));
    }

    // A paid order is cancelled by refunding what is left (SPEC-0007 criterion 14): orders.manage only, with a reason. The
    // order waits in cancellation_pending; the Worker asks Payments for the refund, whose event finishes the order.
    private async Task<OrderDetails> CancelPaidAsync(Order order, string? reason, int attempt, CancellationToken cancellationToken)
    {
        if (!context.HasPermission(TenantPermissions.OrdersManage))
        {
            throw new AppException(OrderErrors.RefundPermissionRequired);
        }

        if (order.Status != OrderStatus.CancellationPending)
        {
            if (Order.CheckCancellationReason(reason) is { } error)
            {
                throw new AppException(error);
            }

            order.NextOrThrow(OrderTrigger.CancelPaid);
            // Audited as requested by the member, by membership id only (SPEC-0009 criterion 2).
            var audit = OutgoingMessage.From(AuditRecordedV1.Of(
                AuditActions.OrderCancellationRequested, AuditActor.Member(context.MembershipId), AuditTargetTypes.Order, order.Id, clock.GetUtcNow()));
            if (!await store.TryRequestCancellationAsync(order.Id, order.Status, reason!.Trim(), context.MembershipId, audit, cancellationToken))
            {
                // A refund moved it between the read and the write: decide again on what it is now.
                return await CancelAsync(order.Id, reason, attempt + 1, cancellationToken);
            }
        }

        // Asking again while the cancellation runs is answered with the order as it is.
        return OrderDetails.From(await FindVisibleAsync(order.Id, cancellationToken));
    }

    private async Task<OrderRecord> FindVisibleAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await store.FindAsync(id, cancellationToken);
        return record is not null && (record.Order.IsOwnedBy(context.MembershipId) || context.HasPermission(TenantPermissions.OrdersManage))
            ? record
            : throw new AppException(OrderErrors.NotFound);
    }

    private async Task<OrderRecord> FindOwnAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await store.FindAsync(id, cancellationToken);
        return record is not null && record.Order.IsOwnedBy(context.MembershipId) ? record : throw new AppException(OrderErrors.NotFound);
    }

    // The catalog answers for the current tenant only, so another tenant's product is missing, exactly like an unknown one.
    private async Task<IReadOnlyList<OrderLineDraft>> SnapshotAsync(OrderLinesCommand command, CancellationToken cancellationToken)
    {
        var requested = command.Lines!.Select(static l => (ProductId: l!.ProductId!.Value, Quantity: l.Quantity!.Value)).ToList();
        var products = (await catalog.GetSnapshotsAsync([.. requested.Select(static l => l.ProductId)], cancellationToken))
            .ToDictionary(static p => p.ProductId);
        if (requested.Any(l => !products.ContainsKey(l.ProductId)))
        {
            throw new AppException(CatalogContractErrors.ProductNotFound);
        }

        if (products.Values.Any(static p => !p.IsActive))
        {
            throw new AppException(OrderErrors.ProductUnavailable);
        }

        return [.. requested.Select(l =>
        {
            var product = products[l.ProductId];
            return new OrderLineDraft(product.ProductId, product.Sku, product.Name, product.PriceAmount, product.PriceCurrency, l.Quantity);
        })];
    }
}
