namespace TenantFlow.Payments.Application.Refunds.Services;

/// <summary>The refund reconciliation schedule (SPEC-0007 criterion 12): the payment one (ADR-0022 decision 7).</summary>
public static class RefundReconciliation
{
    /// <summary>The timer kind, in <c>payments.workflow_timers</c>.</summary>
    public const string TimerKind = "payments.reconcile_refund";
}
