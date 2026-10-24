using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TenantFlow.Api.Tests.Telemetry;

/// <summary>
/// SPEC-0010 criteria 6 and 7 (threat model 0008 DP-A25 and DP-A03, ADR-0031): a query-string value never reaches an
/// exported server span, and a value planted in a third-party exception message or an unlisted log scope never reaches an
/// exported log record. Asserted on the host's real OpenTelemetry pipeline, after every processor the host registers.
/// </summary>
public sealed partial class TelemetryRedactionTests
{
    private const string ProbePath = "/redaction-probe";
    private const string ProbeMessage = "redaction probe fault";
    private const string PlantedQuery = "planted-query-7f3a";
    private const string PlantedExceptionMessage = "planted-exception-41c9";
    private const string PlantedInnerMessage = "planted-inner-0d2e";
    private const string PlantedScope = "planted-scope-b6e1";
    private const string OwnMessage = "TenantFlow's own reviewed message";

    [Fact]
    public async Task A_query_string_value_or_key_never_reaches_the_server_span()
    {
        using var telemetry = new TelemetryCapture();
        // A middleware in front of routing: without one, an unrouted test request's span is sometimes not exported (TelemetryTests).
        await using var factory = ApiFactory.CreateProduction(
            services => services.AddSingleton<IStartupFilter, PassThroughStartupFilter>(),
            telemetry: telemetry);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri($"{ProbePath}?email={PlantedQuery}&{PlantedQuery}=1", UriKind.Relative), TestContext.Current.CancellationToken);
        var span = await telemetry.WaitForSpanAsync(IsProbeSpan, TestContext.Current.CancellationToken);

        Assert.False(span.Tags.ContainsKey("url.query"), "The server span exports url.query.");
        Assert.DoesNotContain(span.Tags, tag => $"{tag.Key}={tag.Value}".Contains(PlantedQuery, StringComparison.Ordinal));
        Assert.DoesNotContain(PlantedQuery, span.DisplayName, StringComparison.Ordinal);
        Assert.Equal(ProbePath, span.Tags["url.path"]);
    }

    [Fact]
    public async Task A_planted_value_in_a_third_party_exception_or_an_unlisted_scope_is_not_exported()
    {
        using var telemetry = new TelemetryCapture();
        await using var factory = ApiFactory.CreateProduction(
            services => services.AddSingleton<IStartupFilter, FaultLoggingStartupFilter>(),
            telemetry: telemetry);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(ProbePath, UriKind.Relative), TestContext.Current.CancellationToken);
        // The third-party fault is logged first, then TenantFlow's own.
        var third = await telemetry.WaitForLogAsync(log => log.Message == ProbeMessage, TestContext.Current.CancellationToken);
        var own = await telemetry.WaitForLogAsync(log => log.Message == ProbeMessage && !ReferenceEquals(log, third), TestContext.Current.CancellationToken);

        foreach (var planted in new[] { PlantedExceptionMessage, PlantedInnerMessage, PlantedScope })
        {
            Assert.DoesNotContain(third.Exported, item => item.Contains(planted, StringComparison.Ordinal));
        }

        // Diagnosis keeps the exception type, the stack, the inner exception's type and the allow-listed scope keys.
        Assert.Equal(typeof(InvalidOperationException).FullName, third.Attributes.GetValueOrDefault("exception.type"));
        Assert.Equal("[redacted]", third.Attributes["exception.message"]);
        Assert.Contains(nameof(ThrowThirdParty), third.Attributes["exception.stacktrace"], StringComparison.Ordinal);
        Assert.Contains(typeof(FormatException).FullName!, third.Attributes["exception.stacktrace"], StringComparison.Ordinal);
        Assert.Equal("kept-job", third.Attributes["Job"]);
        Assert.Equal(ProbePath, third.Attributes["RequestPath"]);
        Assert.False(third.Attributes.ContainsKey("Unlisted"));

        // A TenantFlow exception's message is reviewed source text, so it stays.
        Assert.Equal(OwnMessage, own.Attributes["exception.message"]);
    }

    private static bool IsProbeSpan(CapturedSpan span) =>
        span.Kind == ActivityKind.Server && span.Tags.GetValueOrDefault("url.path") == ProbePath;

    private static void ThrowThirdParty()
    {
        try
        {
            throw new FormatException(PlantedInnerMessage);
        }
        catch (FormatException inner)
        {
            throw new InvalidOperationException($"Value '{PlantedExceptionMessage}' was rejected.", inner);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = ProbeMessage)]
    private static partial void LogFault(ILogger logger, Exception exception);

    private sealed class ProbeFaultException(string message) : Exception(message);

    private sealed class PassThroughStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(static (context, nextMiddleware) => nextMiddleware(context));
            next(app);
        };
    }

    /// <summary>Logs two faults inside the request pipeline, within a scope that holds a listed and an unlisted key.</summary>
    private sealed class FaultLoggingStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Path == ProbePath)
                {
                    var logger = context.RequestServices.GetRequiredService<ILogger<TelemetryRedactionTests>>();
                    using (logger.BeginScope(new Dictionary<string, object> { ["Job"] = "kept-job", ["Unlisted"] = PlantedScope }))
                    {
                        try
                        {
                            ThrowThirdParty();
                        }
                        catch (InvalidOperationException exception)
                        {
                            LogFault(logger, exception);
                        }

                        LogFault(logger, new ProbeFaultException(OwnMessage));
                    }
                }

                await nextMiddleware(context);
            });
            next(app);
        };
    }
}
