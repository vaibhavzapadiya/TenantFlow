using TenantFlow.Api.Endpoints.V1;
using TenantFlow.Api.Endpoints.V1.Me;
using TenantFlow.Api.Endpoints.V1.Payments;
using TenantFlow.Api.Endpoints.V1.Platform.Audit;
using TenantFlow.Api.Endpoints.V1.Platform.Support;
using TenantFlow.Api.Endpoints.V1.Platform.Tenants;
using TenantFlow.Api.Endpoints.V1.Tenants;
using TenantFlow.Api.Endpoints.V1.Tenants.Audit;
using TenantFlow.Api.Endpoints.V1.Tenants.ImportsExports;
using TenantFlow.Api.Endpoints.V1.Tenants.Members;
using TenantFlow.Api.Endpoints.V1.Tenants.Orders;
using TenantFlow.Api.Endpoints.V1.Tenants.Payments;
using TenantFlow.Api.Endpoints.V1.Tenants.Products;
using TenantFlow.Api.Endpoints.V1.Tenants.Stock;
using TenantFlow.Api.Endpoints.V1.Tenants.Subscriptions;
using TenantFlow.BuildingBlocks.Observability;

namespace TenantFlow.Api.Extensions;

/// <summary>Every endpoint the API serves, in one place. A new feature's endpoints are mapped here, on a version group.</summary>
internal static class EndpointExtensions
{
    public static WebApplication MapTenantFlowApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Health stays unversioned: platforms probe it without knowing the API's version.
        app.MapTenantFlowHealthEndpoints();

        // Every business endpoint is mapped on the version group (ADR-0015).
        var v1 = app.MapV1();
        v1.MapMeEndpoints();
        v1.MapTenantLifecycleEndpoints();
        v1.MapTenantAdministratorEndpoints();
        v1.MapTenantEndpoints();
        v1.MapMemberEndpoints();
        v1.MapProductEndpoints();
        v1.MapStockEndpoints();
        v1.MapOrderEndpoints();
        v1.MapPaymentEndpoints();
        v1.MapRefundEndpoints();
        v1.MapPaymentWebhookEndpoints();
        v1.MapPlanEndpoints();
        v1.MapSubscriptionEndpoints();
        v1.MapImportEndpoints();
        v1.MapExportEndpoints();
        v1.MapAuditEndpoints();
        v1.MapPlatformAuditEndpoints();
        v1.MapSupportEndpoints();
        return app;
    }
}
