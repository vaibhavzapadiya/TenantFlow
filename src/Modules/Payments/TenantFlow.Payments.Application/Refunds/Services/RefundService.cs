using System.Globalization;
using FluentValidation;
using TenantFlow.Audit.Contracts;
using TenantFlow.Audit.Contracts.Events;
using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.Payments.Application.Abstractions;
using TenantFlow.Payments.Application.Idempotency;
using TenantFlow.Payments.Application.Refunds.Commands;
using TenantFlow.Payments.Application.Refunds.Dtos;
using TenantFlow.Payments.Domain.Payments;
using TenantFlow.Payments.Domain.Refunds;

namespace TenantFlow.Payments.Application.Refunds.Services;

/// <summary>
/// Refunding an order's payment (SPEC-0007 criteria 9 to 11, ADR-0023 decision 4). The route has checked
/// <c>orders.manage</c>. Committed steps, as for payments: the idempotency key, the refund and its reconciliation timer
/// are committed with the payment locked and the captured amount checked; then the provider is called with the refund id
/// as its idempotency key, outside any transaction; then the outcome is committed with its event. Once the refund is
/// committed the steps no longer follow the client.
/// </summary>
public sealed class RefundService(
    ITenantContext context,
    IPaymentStore store,
    RefundSender sender,
    IValidator<CreateRefundCommand> validator,
    TimeProvider clock)
{
    /// <summary>The operation an idempotency key of this use case is scoped to.</summary>
    public const string CreateOperation = "create_refund";

    /// <exception cref="AppException">
    /// <see cref="PaymentErrors.NotFound"/>, <see cref="PaymentErrors.NotRefundable"/>, <see cref="PaymentErrors.RefundExceedsCaptured"/>,
    /// <see cref="PaymentErrors.IdempotencyKeyReused"/> or <see cref="PaymentErrors.RequestInProgress"/>.
    /// </exception>
    public async Task<RefundDetails> CreateAsync(CreateRefundCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var key = new IdempotencyKeyRef(CreateOperation, command.IdempotencyKey!);
        var hash = RequestHash(command);
        if (await IdempotentRequests.AnswerAsync<RefundDetails>(store, key, hash, cancellationToken) is { } replayed)
        {
            return replayed;
        }

        var payment = await store.FindByOrderAsync(command.OrderId, cancellationToken) ?? throw new AppException(PaymentErrors.NotFound);
        var now = clock.GetUtcNow();
        var refund = Refund.Start(Guid.CreateVersion7(now), payment, command.Amount, Refund.RefundKind, command.Reason, context.MembershipId, now);
        // Audited as requested by the member, by membership id only (SPEC-0009 criterion 2).
        var audit = OutgoingMessage.From(AuditRecordedV1.Of(AuditActions.RefundRequested, AuditActor.Member(context.MembershipId), AuditTargetTypes.Refund, refund.Id, now));
        switch (await store.TryStartRefundAsync(refund, key, hash, audit, cancellationToken))
        {
            case RefundStart.KeyTaken:
                return await IdempotentRequests.AnswerAsync<RefundDetails>(store, key, hash, cancellationToken) ?? throw new AppException(PaymentErrors.RequestInProgress);
            case RefundStart.NotRefundable:
                throw new AppException(PaymentErrors.NotRefundable);
            case RefundStart.ExceedsCaptured:
                throw new AppException(PaymentErrors.RefundExceedsCaptured);
            case RefundStart.Started:
                break;
            case var other:
                // Fail closed: only a stored refund may reach the provider (the other results belong to cancellation refunds).
                throw new InvalidOperationException($"Unexpected refund start result {other}.");
        }

        // Committed: from here on finish regardless of the client (the provider call has its own timeout).
        return await sender.SendAsync(refund, payment, CancellationToken.None);
    }

    /// <summary>The refunds of the order's payment, oldest first.</summary>
    /// <exception cref="AppException"><see cref="PaymentErrors.NotFound"/>.</exception>
    public async Task<IReadOnlyList<RefundDetails>> ListAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var payment = await store.FindByOrderAsync(orderId, cancellationToken) ?? throw new AppException(PaymentErrors.NotFound);
        return [.. (await store.ListRefundsAsync(payment.Id, cancellationToken)).Select(RefundDetails.From)];
    }

    // SHA-256 of the canonical request, bound to the caller's membership (as for payments, ADR-0022 decision 4).
    private string RequestHash(CreateRefundCommand command)
    {
        var canonical = string.Create(CultureInfo.InvariantCulture, $"{context.MembershipId:D}\n{command.OrderId:D}\n{command.Amount:0.####}\n{command.Reason?.Trim()}");
        return IdempotentRequests.Hash(canonical);
    }
}
