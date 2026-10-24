namespace TenantFlow.Payments.Application.Abstractions;

/// <summary>What <see cref="IPaymentStore.TryStartAsync"/> did.</summary>
public enum StartResult
{
    /// <summary>The key is claimed and the payment stored.</summary>
    Started = 1,

    /// <summary>Another request already holds the key; nothing was written.</summary>
    KeyTaken = 2,

    /// <summary>The order already has a payment; nothing was written.</summary>
    PaymentExists = 3,
}
