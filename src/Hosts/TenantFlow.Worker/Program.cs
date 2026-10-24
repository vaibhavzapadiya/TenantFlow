// TenantFlow worker host. Health rules and heartbeat: SPEC-0001. Outbox publishing: SPEC-0005.

using TenantFlow.BuildingBlocks.Messaging.Outbox;
using TenantFlow.BuildingBlocks.Observability;
using TenantFlow.BuildingBlocks.Scheduling;
using TenantFlow.ImportsExports.Application.Observability;
using TenantFlow.Orders.Application.Observability;
using TenantFlow.Payments.Application.Observability;
using TenantFlow.Worker.Health;
using TenantFlow.Worker.Heartbeat;
using TenantFlow.Worker.Messaging;

// The deployed container's health check runs this assembly again with one argument (LivenessProbe).
if (args is [LivenessProbe.Argument])
{
    Environment.ExitCode = await LivenessProbe.RunAsync(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS"), CancellationToken.None);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

// Same composition rules as the API: fail fast on DI mistakes in every environment.
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});

builder.AddTenantFlowTelemetry(
    "tenantflow-worker",
    WorkerTelemetry.SourceName,
    OutboxMetrics.MeterName,
    ImportsExportsMetrics.MeterName,
    SchedulingMetrics.MeterName,
    PaymentsMetrics.MeterName,
    OrdersMetrics.MeterName);

builder.Services.AddOptions<HeartbeatOptions>()
    .BindConfiguration(HeartbeatOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IHeartbeatRecorder, HeartbeatRecorder>();
builder.Services.AddHostedService<PlatformHeartbeatService>();

// Readiness watches PostgreSQL and RabbitMQ (SPEC-0005 criterion 7).
builder.AddWorkerMessaging();

var app = builder.Build();

app.MapTenantFlowHealthEndpoints();

await app.RunAsync();
