using TenantFlow.Audit.Infrastructure;
using TenantFlow.BuildingBlocks.Observability;
using TenantFlow.Catalog.Infrastructure;
using TenantFlow.IdentityAccess.Infrastructure;
using TenantFlow.ImportsExports.Infrastructure;
using TenantFlow.Inventory.Infrastructure;
using TenantFlow.Operations.Infrastructure;
using TenantFlow.Orders.Infrastructure;
using TenantFlow.Payments.Infrastructure;
using TenantFlow.Subscriptions.Infrastructure;
using TenantFlow.Tenants.Infrastructure;

namespace TenantFlow.Api.Extensions;

/// <summary>Database registration for the API: settings, modules, and the readiness check (ADR-0012, ADR-0017).</summary>
internal static class PersistenceExtensions
{
    /// <summary>The database settings, the modules that use them, and the readiness check that watches them.</summary>
    public static WebApplicationBuilder AddTenantFlowPersistence(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Modules connect as the application role (ADR-0012).
        var connectionString = builder.Services.AddTenantFlowDatabase(builder.Configuration);
        builder.Services.AddTenantsModule(connectionString);
        builder.Services.AddIdentityAccessModule(connectionString);
        builder.Services.AddCatalogModule(connectionString);
        builder.Services.AddInventoryModule(connectionString);
        builder.Services.AddOrdersModule(connectionString);
        builder.Services.AddPaymentsModule(connectionString);
        builder.Services.AddSubscriptionsModule(connectionString);
        builder.Services.AddImportsExportsModule(connectionString);
        builder.Services.AddAuditModule(connectionString);
        builder.Services.AddOperationsModule(connectionString);
        return builder;
    }
}
