using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TenantFlow.Api.Tests.Hosting;

/// <summary>
/// SPEC-0001 criteria 14 to 16 for the API: resource attributes on spans, logs correlated with the
/// request span, and requests unaffected when the collector is unreachable.
/// </summary>
public sealed partial class TelemetryTests
{
    private const string ProbePath = "/telemetry-probe";
    private const string ProbeMessage = "telemetry probe log";

    // Known test-host issue (open): without a middleware in front of routing, the span of an
    // unrouted test request was sometimes never exported (about 1 run in 5). With the probe
    // middleware it was captured in every run. Production hosts are unaffected; investigate
    // before relying on 404 spans in tests.
    [Fact]
    public async Task Request_span_has_the_service_resource_and_its_log_carries_the_span_ids()
    {
        using var telemetry = new TelemetryCapture();
        await using var factory = ApiFactory.CreateProduction(
            services => services.AddSingleton<IStartupFilter, ProbeLoggingStartupFilter>(),
            telemetry: telemetry);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(ProbePath, UriKind.Relative), TestContext.Current.CancellationToken);
        var span = await telemetry.WaitForSpanAsync(IsProbeRequestSpan, TestContext.Current.CancellationToken);
        var log = await telemetry.WaitForLogAsync(record => record.Message == ProbeMessage, TestContext.Current.CancellationToken);

        // Criterion 14: resource attributes.
        Assert.Equal("tenantflow-api", span.Resource["service.name"]);
        Assert.Equal("tenantflow", span.Resource["service.namespace"]);
        Assert.False(string.IsNullOrWhiteSpace(span.Resource["service.version"] as string));
        Assert.Equal("Production", span.Resource["deployment.environment.name"]);

        // Criterion 15: the log written during the request carries the request span's IDs.
        Assert.NotEqual(default, span.TraceId);
        Assert.Equal(span.TraceId, log.TraceId);
        Assert.Equal(span.SpanId, log.SpanId);
    }

    [Fact]
    public async Task Requests_succeed_while_the_collector_is_unreachable()
    {
        // ApiFactory points the OTLP exporter at a closed port, so every export fails.
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();

        for (var i = 0; i < 20; i++)
        {
            using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = ProbeMessage)]
    private static partial void LogProbe(ILogger logger);

    private static bool IsProbeRequestSpan(CapturedSpan span) =>
        // Unrouted requests are named only by method ("GET"), so match the standard url.path tag.
        span.Kind == ActivityKind.Server && span.Tags.GetValueOrDefault("url.path") == ProbePath;

    /// <summary>Writes one log line inside the request pipeline, where the request span is current.</summary>
    private sealed class ProbeLoggingStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Path == ProbePath)
                {
                    LogProbe(context.RequestServices.GetRequiredService<ILogger<TelemetryTests>>());
                }

                await nextMiddleware(context);
            });
            next(app);
        };
    }
}
