namespace TenantFlow.Api.Tests.Telemetry;

/// <summary>
/// SPEC-0010 criteria 11 and 14: the host publishes its health checks as <c>tenantflow.health.status</c>, so the
/// database-down alert can fire. The test host's database is a closed port, so the check reports 0.
/// </summary>
public sealed class HealthStatusTelemetryTests
{
    [Fact]
    public async Task An_unreachable_database_is_published_as_status_0_for_the_postgresql_check()
    {
        using var meter = new MeterCapture("tenantflow.health.status");
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!meter.Measurements.Any(static m => m.Tags.GetValueOrDefault("check") == "postgresql") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, TestContext.Current.CancellationToken);
            meter.RecordObservableInstruments();
        }

        var status = Assert.Single(meter.Measurements.Where(static m => m.Tags.GetValueOrDefault("check") == "postgresql").TakeLast(1));
        Assert.Equal(0, status.Value);
        Assert.Equal(["check"], status.Tags.Keys);
    }
}
