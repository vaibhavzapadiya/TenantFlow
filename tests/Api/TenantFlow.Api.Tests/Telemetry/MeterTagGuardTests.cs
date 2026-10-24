using System.Diagnostics.Metrics;

namespace TenantFlow.Api.Tests.Telemetry;

/// <summary>
/// SPEC-0010 criterion 5: the guard every host-test assembly runs (<see cref="MeterTagGuardFixture"/>) reports a tag key
/// outside the allow-list and a GUID tag value, and accepts allow-listed labels. Uses a meter prefix of its own, so these
/// planted violations never reach the assembly's real guard.
/// </summary>
public sealed class MeterTagGuardTests
{
    private const string Prefix = "GuardSelfTest.";

    [Fact]
    public void An_unlisted_tag_key_and_a_guid_tag_value_are_reported()
    {
        using var guard = new MeterTagGuard(Prefix);
        using var meter = new Meter(Prefix + "Meter");
        var counter = meter.CreateCounter<long>("guard.probe");

        counter.Add(1, new KeyValuePair<string, object?>("tenant_id", "t"));
        counter.Add(1, new KeyValuePair<string, object?>("outcome", Guid.NewGuid().ToString()));
        meter.CreateObservableGauge("guard.gauge", static () => new Measurement<long>(1, new KeyValuePair<string, object?>("module", Guid.NewGuid())));

        Assert.Equal(
            [
                "guard.gauge: tag 'module' carries a GUID value",
                "guard.probe: tag 'outcome' carries a GUID value",
                "guard.probe: tag key 'tenant_id' is not in the allow-list",
            ],
            guard.Violations);
    }

    [Fact]
    public void Allow_listed_labels_with_closed_values_pass()
    {
        using var guard = new MeterTagGuard(Prefix);
        using var meter = new Meter(Prefix + "Meter");

        meter.CreateCounter<long>("guard.probe").Add(1, new KeyValuePair<string, object?>("outcome", "processed"), new KeyValuePair<string, object?>("module", "orders"));

        Assert.Empty(guard.Violations);
    }
}
