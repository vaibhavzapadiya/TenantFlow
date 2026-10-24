namespace TenantFlow.Payments.Application.Payments.Services;

/// <summary>The reconciliation schedule (ADR-0022 decision 7, SPEC-0006 criterion 12).</summary>
public static class PaymentReconciliation
{
    /// <summary>The timer kind, in <c>payments.workflow_timers</c>.</summary>
    public const string TimerKind = "payments.reconcile";

    /// <summary>Checks without a final answer before the payment needs review.</summary>
    public const int MaxChecks = 5;

    /// <summary>The first check, after the creating request has had time to finish.</summary>
    public static readonly TimeSpan FirstCheckAfter = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>How often a payment under review is still checked: a late answer settles it.</summary>
    public static readonly TimeSpan ReviewInterval = TimeSpan.FromMinutes(10);
}
