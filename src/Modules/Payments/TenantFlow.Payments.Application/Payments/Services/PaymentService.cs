using System.Globalization;
using FluentValidation;
using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.Orders.Contracts;
using TenantFlow.Payments.Application.Abstractions;
using TenantFlow.Payments.Application.Idempotency;
using TenantFlow.Payments.Application.Payments.Commands;
using TenantFlow.Payments.Application.Payments.Dtos;
using TenantFlow.Payments.Domain.Payments;

namespace TenantFlow.Payments.Application.Payments.Services;

/// <summary>
/// Paying an order (SPEC-0006 criteria 1 to 6). The route has checked <c>orders.place</c>; Orders checks the caller owns
/// the order. Creation is committed steps (ADR-0022 decision 3, reordered by the M6 review): Orders says what to charge
/// (a read); the idempotency key, the payment and its reconciliation timer are committed; Orders marks the payment begun,
/// so the order can no longer be cancelled; then the provider is called, outside any transaction, with the payment id as
/// its idempotency key, and the outcome is committed with its event. A begun mark therefore always has a payment behind
/// it. A timeout is an unknown outcome, never a failure, and is never retried here: reconciliation settles it. Once the
/// payment is committed, the steps no longer follow the client: a disconnect cannot cut a charge off half-way.
/// </summary>
public sealed class PaymentService(
    ITenantContext context,
    IPaymentStore store,
    IOrderPayments orders,
    PaymentProviders providers,
    IValidator<CreatePaymentCommand> validator,
    TimeProvider clock)
{
    /// <summary>The operation an idempotency key of this use case is scoped to.</summary>
    public const string CreateOperation = "create_payment";

    /// <summary>The failure code of a payment whose order stopped waiting for payment before it was charged.</summary>
    public const string OrderNotPayable = "order_not_payable";

    /// <exception cref="AppException">
    /// <see cref="PaymentErrors.IdempotencyKeyReused"/>, <see cref="PaymentErrors.RequestInProgress"/>,
    /// <see cref="PaymentErrors.AlreadyExists"/>, <see cref="PaymentErrors.NothingToPay"/>, or an Orders error
    /// (<c>orders.order_not_found</c>, <c>orders.invalid_transition</c>).
    /// </exception>
    public async Task<PaymentDetails> CreateAsync(CreatePaymentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var key = new IdempotencyKeyRef(CreateOperation, command.IdempotencyKey!);
        var hash = RequestHash(command);

        // A known key is answered from what it stored, before Orders is asked: the order has moved on since.
        if (await IdempotentRequests.AnswerAsync<PaymentDetails>(store, key, hash, cancellationToken) is { } replayed)
        {
            return replayed;
        }

        var order = await orders.GetPayableAsync(command.OrderId, cancellationToken);
        if (order.Amount <= 0)
        {
            throw new AppException(PaymentErrors.NothingToPay);
        }

        var provider = providers.Current;
        var now = clock.GetUtcNow();
        var payment = Payment.Start(Guid.CreateVersion7(now), context.TenantId, order.OrderId, context.MembershipId, order.Amount, order.Currency, provider.Name, command.PaymentMethod!, now);
        switch (await store.TryStartAsync(payment, key, hash, cancellationToken))
        {
            case StartResult.KeyTaken:
                // A concurrent request with this key won the claim: it finished (replay), or it is still running (409).
                return await IdempotentRequests.AnswerAsync<PaymentDetails>(store, key, hash, cancellationToken) ?? throw new AppException(PaymentErrors.RequestInProgress);
            case StartResult.PaymentExists:
                throw new AppException(PaymentErrors.AlreadyExists);
            case StartResult.Started:
                break;
            case var other:
                // Fail closed: only a stored payment may reach the provider.
                throw new InvalidOperationException($"Unexpected payment start result {other}.");
        }

        // Committed: from here on finish regardless of the client (the provider call has its own timeout).
        var committed = CancellationToken.None;
        if (!await orders.BeginPaymentAsync(order.OrderId, committed))
        {
            // Cancelled or expired between the read and now: nothing was sent, so nothing may be charged.
            return await store.ApplyAsync(payment.Id, PaymentTrigger.Failed, null, OrderNotPayable, committed);
        }

        return (await PaymentSender.SendAsync(store, provider, payment, committed)).Settled;
    }

    /// <summary>The order's payment, for its owner or a holder of <c>orders.manage</c>; to anyone else it does not exist.</summary>
    /// <exception cref="AppException"><see cref="PaymentErrors.NotFound"/>.</exception>
    public async Task<PaymentDetails> GetForOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var payment = await store.FindByOrderAsync(orderId, cancellationToken);
        return payment is not null && (payment.IsOwnedBy(context.MembershipId) || context.HasPermission(TenantPermissions.OrdersManage))
            ? PaymentDetails.From(payment)
            : throw new AppException(PaymentErrors.NotFound);
    }

    // SHA-256 of the canonical request: what the key promises to mean. The caller's membership is part of it, so another
    // member of the tenant who sends the same key gets 422, never someone else's stored response.
    private string RequestHash(CreatePaymentCommand command)
    {
        var canonical = string.Create(CultureInfo.InvariantCulture, $"{context.MembershipId:D}\n{command.OrderId:D}\n{command.PaymentMethod}");
        return IdempotentRequests.Hash(canonical);
    }
}
