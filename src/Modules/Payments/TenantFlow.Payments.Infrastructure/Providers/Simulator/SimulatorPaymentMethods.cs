namespace TenantFlow.Payments.Infrastructure.Providers.Simulator;

/// <summary>
/// The simulator's payment-method tokens (SPEC-0006 criterion 14), like a provider's test cards: each scripts one outcome,
/// so every failure mode is reproducible without a network. Any other token is declined as unsupported.
/// </summary>
public static class SimulatorPaymentMethods
{
    /// <summary>Charged at once.</summary>
    public const string Success = "sim_success";

    /// <summary>Declined at once (<c>card_declined</c>).</summary>
    public const string Decline = "sim_decline";

    /// <summary>The create call times out, but the provider did charge: a later status check or webhook says succeeded.</summary>
    public const string TimeoutThenSuccess = "sim_timeout_then_success";

    /// <summary>The create call times out and the provider declined: a later status check or webhook says failed.</summary>
    public const string TimeoutThenDecline = "sim_timeout_then_decline";

    /// <summary>The create call times out and the provider never gives a final answer.</summary>
    public const string TimeoutUnknown = "sim_timeout_unknown";

    /// <summary>The create call fails with a 5xx before the provider recorded anything.</summary>
    public const string ServerError = "sim_server_error";

    /// <summary>Charged at once; every refund of it is declined (<c>refund_declined</c>) (SPEC-0007).</summary>
    public const string RefundDecline = "sim_refund_decline";

    /// <summary>Charged at once; a refund call times out, and the provider did refund: a status check or webhook says so.</summary>
    public const string RefundTimeout = "sim_refund_timeout";
}
