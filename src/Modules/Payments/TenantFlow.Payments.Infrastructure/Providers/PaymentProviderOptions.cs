namespace TenantFlow.Payments.Infrastructure.Providers;

/// <summary>Which provider charges new payments (section <c>Payments</c>, key <c>Provider</c>): <c>simulator</c> (default) or <c>stripe</c>.</summary>
public sealed class PaymentProviderOptions
{
    public const string SectionName = "Payments";

    public string Provider { get; set; } = Simulator.SimulatedPaymentProvider.ProviderName;
}
