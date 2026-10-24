using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TenantFlow.Testing;

namespace TenantFlow.Worker.Tests;

/// <summary>
/// SPEC-0001 criteria 14 and 15 for the worker: heartbeat spans carry the worker's resource
/// attributes, and the heartbeat log line carries the heartbeat span's IDs.
/// </summary>
public sealed class WorkerTelemetryTests
{
    [Fact]
    public async Task Heartbeat_log_carries_the_heartbeat_span_ids_and_the_worker_resource()
    {
        var time = new FakeTimeProvider();
        using var telemetry = new TelemetryCapture();
        await using var factory = WorkerFactory.CreateProduction(
            configureServices: services => services.AddSingleton<TimeProvider>(time),
            telemetry: telemetry);
        using var client = factory.CreateClient();

        time.Advance(TimeSpan.FromSeconds(30));
        var log = await telemetry.WaitForLogAsync(
            record => record.Message == "Worker heartbeat 1", TestContext.Current.CancellationToken);
        var span = await telemetry.WaitForSpanAsync(
            record => record.OperationName == "worker.heartbeat" && record.TraceId == log.TraceId,
            TestContext.Current.CancellationToken);

        Assert.NotEqual(default, log.TraceId);
        Assert.Equal(span.SpanId, log.SpanId);
        Assert.Equal("tenantflow-worker", span.Resource["service.name"]);
        Assert.Equal("tenantflow", span.Resource["service.namespace"]);
        Assert.Equal("Production", span.Resource["deployment.environment.name"]);
    }
}
