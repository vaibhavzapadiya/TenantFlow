namespace TenantFlow.Payments.Domain.Payments;

/// <summary>What can be learned about a payment, from the provider's answer, a webhook or reconciliation.</summary>
public enum PaymentTrigger
{
    /// <summary>The provider reports the charge succeeded.</summary>
    Succeeded = 1,

    /// <summary>The provider reports the charge failed (declined).</summary>
    Failed = 2,

    /// <summary>The provider has the payment and is still working on it.</summary>
    Pending = 3,

    /// <summary>The provider gave no usable answer (timeout, 5xx): the outcome is unknown.</summary>
    OutcomeUnknown = 4,

    /// <summary>Reconciliation gave up waiting for a final answer.</summary>
    ReviewRequired = 5,
}
