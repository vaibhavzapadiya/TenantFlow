// TenantFlow API host. Composition rules: ADR-0017; health and configuration: SPEC-0001, ADR-0008;
// authentication and the platform boundary: ADR-0009, ADR-0010; errors: ADR-0016; versioning: ADR-0015;
// OpenAPI: ADR-0019.

using TenantFlow.Api.Extensions;
using TenantFlow.Api.Telemetry;
using TenantFlow.BuildingBlocks.Observability;
using TenantFlow.Operations.Application.Observability;
using TenantFlow.Orders.Application.Observability;
using TenantFlow.Payments.Application.Observability;

var builder = WebApplication.CreateBuilder(args);

builder.ConfigureTenantFlowHost();
builder.AddTenantFlowTelemetry(
    "tenantflow-api",
    AuthDenialRecorder.MeterName,
    PaymentsMetrics.MeterName,
    OrdersMetrics.MeterName,
    OperationsMetrics.MeterName);
builder.AddTenantFlowPersistence();
builder.AddTenantFlowAuthentication();
builder.AddTenantFlowAuditing();
builder.Services.AddTenantFlowProblemDetails();
builder.Services.AddTenantFlowOpenApi();

var app = builder.Build();

app.UseTenantFlowPipeline();
app.MapTenantFlowApi();
app.MapTenantFlowOpenApi();

await app.RunAsync();
