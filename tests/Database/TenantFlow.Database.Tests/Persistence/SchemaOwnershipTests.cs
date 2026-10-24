using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TenantFlow.Audit.Infrastructure.Persistence;
using TenantFlow.BuildingBlocks.Scheduling.Persistence;
using TenantFlow.Catalog.Infrastructure.Persistence;
using TenantFlow.IdentityAccess.Infrastructure.Persistence;
using TenantFlow.ImportsExports.Infrastructure.Persistence;
using TenantFlow.Inventory.Infrastructure.Persistence;
using TenantFlow.Notifications.Infrastructure.Persistence;
using TenantFlow.Operations.Infrastructure.Persistence;
using TenantFlow.Orders.Infrastructure.Persistence;
using TenantFlow.Payments.Infrastructure.Persistence;
using TenantFlow.Subscriptions.Infrastructure.Persistence;
using TenantFlow.Tenants.Infrastructure.Persistence;

namespace TenantFlow.Database.Tests.Persistence;

/// <summary>
/// SPEC-0002 criterion 2 (ADR-0012): each module's migrations create objects only in its own schema,
/// and no foreign key crosses module schemas.
/// </summary>
public sealed class SchemaOwnershipTests
{
    // Every schema and relation that is not PostgreSQL's own, with the schema it lives in.
    private const string UserObjects = """
        SELECT nspname FROM pg_namespace
        WHERE nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'public') AND nspname NOT LIKE 'pg_temp%' AND nspname NOT LIKE 'pg_toast_temp%'
        UNION ALL
        SELECT n.nspname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast') AND n.nspname NOT LIKE 'pg_temp%' AND n.nspname NOT LIKE 'pg_toast_temp%'
        """;

    public static TheoryData<string> Modules => [TenantsDbContext.Schema, IdentityAccessDbContext.Schema, CatalogDbContext.Schema, InventoryDbContext.Schema, OrdersDbContext.Schema, NotificationsDbContext.Schema, PaymentsDbContext.Schema, SubscriptionsDbContext.Schema, SchedulerDbContext.Schema, ImportsExportsDbContext.Schema, AuditDbContext.Schema, OperationsDbContext.Schema];

    [Theory]
    [MemberData(nameof(Modules))]
    public async Task Module_migrations_create_objects_only_in_the_module_schema(string schema)
    {
        await using var database = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);

        await MigrateOnlyAsync(schema, database.OwnerConnectionString);

        var objectSchemas = await MigratorRun.QueryAsync<string>(database.OwnerConnectionString, UserObjects);
        Assert.NotEmpty(objectSchemas);
        Assert.All(objectSchemas, objectSchema => Assert.Equal(schema, objectSchema));
    }

    [Fact]
    public async Task No_foreign_key_crosses_module_schemas()
    {
        await using var database = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var (exitCode, _) = await MigratorRun.RunAsync(database.OwnerConnectionString);
        Assert.Equal(0, exitCode);

        var crossing = await MigratorRun.QueryAsync<string>(
            database.OwnerConnectionString,
            """
            SELECT con.conname::text FROM pg_constraint con
            JOIN pg_class source ON source.oid = con.conrelid
            JOIN pg_class target ON target.oid = con.confrelid
            WHERE con.contype = 'f' AND source.relnamespace <> target.relnamespace
            """);

        Assert.Empty(crossing);
    }

    private static async Task MigrateOnlyAsync(string schema, string connectionString)
    {
        var services = new ServiceCollection();
        if (schema == TenantsDbContext.Schema)
        {
            services.AddTenantsPersistence(connectionString);
        }
        else if (schema == CatalogDbContext.Schema)
        {
            services.AddCatalogPersistence(connectionString);
        }
        else if (schema == InventoryDbContext.Schema)
        {
            services.AddInventoryPersistence(connectionString);
        }
        else if (schema == OrdersDbContext.Schema)
        {
            services.AddOrdersPersistence(connectionString);
        }
        else if (schema == NotificationsDbContext.Schema)
        {
            services.AddNotificationsPersistence(connectionString);
        }
        else if (schema == PaymentsDbContext.Schema)
        {
            services.AddPaymentsPersistence(connectionString);
        }
        else if (schema == SubscriptionsDbContext.Schema)
        {
            services.AddSubscriptionsPersistence(connectionString);
        }
        else if (schema == SchedulerDbContext.Schema)
        {
            services.AddSchedulerPersistence(connectionString);
        }
        else if (schema == ImportsExportsDbContext.Schema)
        {
            services.AddImportsExportsPersistence(connectionString);
        }
        else if (schema == AuditDbContext.Schema)
        {
            services.AddAuditPersistence(connectionString);
        }
        else if (schema == OperationsDbContext.Schema)
        {
            services.AddOperationsPersistence(connectionString);
        }
        else
        {
            services.AddIdentityAccessPersistence(connectionString);
        }

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        DbContext context = schema switch
        {
            TenantsDbContext.Schema => scope.ServiceProvider.GetRequiredService<TenantsDbContext>(),
            CatalogDbContext.Schema => scope.ServiceProvider.GetRequiredService<CatalogDbContext>(),
            InventoryDbContext.Schema => scope.ServiceProvider.GetRequiredService<InventoryDbContext>(),
            OrdersDbContext.Schema => scope.ServiceProvider.GetRequiredService<OrdersDbContext>(),
            NotificationsDbContext.Schema => scope.ServiceProvider.GetRequiredService<NotificationsDbContext>(),
            PaymentsDbContext.Schema => scope.ServiceProvider.GetRequiredService<PaymentsDbContext>(),
            SubscriptionsDbContext.Schema => scope.ServiceProvider.GetRequiredService<SubscriptionsDbContext>(),
            SchedulerDbContext.Schema => scope.ServiceProvider.GetRequiredService<SchedulerDbContext>(),
            ImportsExportsDbContext.Schema => scope.ServiceProvider.GetRequiredService<ImportsExportsDbContext>(),
            AuditDbContext.Schema => scope.ServiceProvider.GetRequiredService<AuditDbContext>(),
            OperationsDbContext.Schema => scope.ServiceProvider.GetRequiredService<OperationsDbContext>(),
            _ => scope.ServiceProvider.GetRequiredService<IdentityAccessDbContext>(),
        };
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }
}
