using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TenantFlow.Worker.Tests;

/// <summary>
/// SPEC-0001 criterion 10, SPEC-0005 criterion 7 (readiness watches the broker), and the worker's configuration
/// contract. The host runs in Production so no Development-only default hides a problem.
/// </summary>
public sealed class WorkerHostTests
{
    [Fact]
    public async Task Liveness_returns_200_with_only_the_status_word_even_when_dependencies_are_down()
    {
        await using var factory = WorkerFactory.CreateProduction();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", body);
    }

    [Fact]
    public async Task Readiness_is_503_while_the_broker_is_unreachable()
    {
        // Only the broker check runs, so the result is the broker's alone.
        await using var factory = WorkerFactory.CreateProduction(configureServices: static services =>
            services.Configure<HealthCheckServiceOptions>(static options =>
                options.Registrations.Remove(options.Registrations.Single(static r => r.Name == "postgresql"))));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", body);
    }

    // SPEC-0008 criterion 11 and SPEC-0009 criteria 8 and 12: the Worker's scheduler fires the imports and exports metrics
    // task every 30 seconds, the storage sweep every hour and the retention run every day; SPEC-0010 criterion 4: the
    // payments metrics task every 30 seconds.
    [Fact]
    public async Task The_scheduler_runs_the_metrics_task_the_storage_sweep_and_retention_on_their_schedules()
    {
        await using var factory = WorkerFactory.CreateProduction();

        var jobs = factory.Services.GetRequiredService<TenantFlow.BuildingBlocks.Scheduling.SchedulerJobs>();

        Assert.Equal(
            [("imports_exports.metrics", "0/30 * * * * ?"), ("imports_exports.storage_sweep", "0 5 * * * ?"), ("payments.metrics", "15/30 * * * * ?"), ("retention", "0 15 3 * * ?")],
            jobs.Tasks.Select(static task => (task.Name, task.CronExpression)));
        Assert.Contains("imports_exports", jobs.Schemas);
    }

    // SPEC-0009 criterion 19: outside Development, EF Core logs nothing below Warning, so SQL text never reaches the logs.
    [Fact]
    public async Task Outside_development_entity_framework_core_logs_warnings_and_above_only()
    {
        await using var factory = WorkerFactory.CreateProduction();

        var logger = factory.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("Microsoft.EntityFrameworkCore.Database.Command");

        Assert.Equal((false, true), (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Information), logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Warning)));
    }

    [Fact]
    public async Task Host_refuses_to_start_without_a_broker_password_and_names_the_key()
    {
        await using var factory = WorkerFactory.CreateProduction(
            new Dictionary<string, string> { ["Messaging:RabbitMq:Password"] = string.Empty });

        var exception = Record.Exception(() => factory.CreateClient());

        // One assertion that prints what the host actually did: this test failed once under load and never again in
        // sixteen runs, so a failure must carry its own evidence.
        var text = exception?.ToString() ?? "(the host started)";
        Assert.True(
            text.Contains("Messaging:RabbitMq:Password", StringComparison.Ordinal) && !text.Contains("placeholder", StringComparison.Ordinal),
            $"Expected a startup failure naming the key and no credential value, got: {text}");
    }

    // SPEC-0012 criterion 7 (MW-A19): a broker that is not on this machine is reached only over TLS.
    [Fact]
    public async Task Host_refuses_to_start_with_plain_amqp_to_a_remote_broker_and_names_the_key()
    {
        await using var factory = WorkerFactory.CreateProduction(
            new Dictionary<string, string> { ["Messaging:RabbitMq:Host"] = "broker.example.invalid" });

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        Assert.Contains("Messaging:RabbitMq:UseTls", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_starts_with_tls_to_a_remote_broker()
    {
        await using var factory = WorkerFactory.CreateProduction(new Dictionary<string, string>
        {
            ["Messaging:RabbitMq:Host"] = "broker.example.invalid",
            ["Messaging:RabbitMq:UseTls"] = "true",
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/api")]
    [InlineData("/health")]
    public async Task Every_other_path_returns_404(string path)
    {
        await using var factory = WorkerFactory.CreateProduction();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("01:00:01")]
    public async Task Host_refuses_to_start_with_a_heartbeat_interval_outside_one_second_to_one_hour(string interval)
    {
        await using var factory = WorkerFactory.CreateProduction(
            new Dictionary<string, string> { ["Worker:HeartbeatInterval"] = interval });

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        Assert.Contains("Worker:HeartbeatInterval", exception.ToString(), StringComparison.Ordinal);
    }
}
