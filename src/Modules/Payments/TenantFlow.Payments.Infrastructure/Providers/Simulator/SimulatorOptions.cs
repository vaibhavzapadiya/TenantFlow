namespace TenantFlow.Payments.Infrastructure.Providers.Simulator;

/// <summary>
/// The simulator's settings (section <c>Payments:Simulator</c>). The webhook secret comes from user-secrets or the
/// environment, never a committed file (ADR-0008); without it every simulator webhook is refused (fail closed). The
/// simulator charges nothing, so it takes payments only where <see cref="Enabled"/> is set (Development and tests); a host
/// configured for it anywhere else refuses to start.
/// </summary>
public sealed class SimulatorOptions
{
    public const string SectionName = "Payments:Simulator";

    public string? WebhookSecret { get; set; }

    public bool Enabled { get; set; }
}
