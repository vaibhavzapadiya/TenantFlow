namespace TenantFlow.Payments.Application.Abstractions;

/// <summary>What <see cref="IPaymentStore.TryStartRefundAsync"/> did.</summary>
public enum RefundStart
{
    /// <summary>The refund (and its key, when given) is stored.</summary>
    Started = 1,

    /// <summary>Another request already holds the key; nothing was written.</summary>
    KeyTaken = 2,

    /// <summary>The payment has not succeeded; nothing was written.</summary>
    NotRefundable = 3,

    /// <summary>The refund would take the refunds past the captured amount (or nothing is left); nothing was written.</summary>
    ExceedsCaptured = 4,

    /// <summary>The payment already has a cancellation refund that has not failed; nothing was written.</summary>
    CancellationExists = 5,

    /// <summary>
    /// Another refund of the payment is not settled yet, so what is left is not known; nothing was written (review fix R4).
    /// </summary>
    RefundsUnsettled = 6,
}
