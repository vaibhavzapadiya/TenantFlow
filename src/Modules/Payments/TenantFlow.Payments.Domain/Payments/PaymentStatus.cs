namespace TenantFlow.Payments.Domain.Payments;

/// <summary>Where a payment is (SPEC-0006, ADR-0022 decision 2). <see cref="PaymentStateMachine"/> says how it moves.</summary>
public enum PaymentStatus
{
    /// <summary>Committed before the provider is called; waiting for the provider's answer.</summary>
    Processing = 1,

    /// <summary>The provider charged it. Final.</summary>
    Succeeded = 2,

    /// <summary>The provider declined it. Final.</summary>
    Failed = 3,

    /// <summary>The provider call timed out or failed with no answer: it may or may not have charged. Reconciled, never retried.</summary>
    Unknown = 4,

    /// <summary>Reconciliation found no final answer in time; a late answer still settles it.</summary>
    RequiresReview = 5,
}
