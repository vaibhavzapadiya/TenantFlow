using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TenantFlow.BuildingBlocks.Messaging.Timers;
using TenantFlow.Catalog.Infrastructure;
using TenantFlow.IdentityAccess.Application.TenantAccess;
using TenantFlow.IdentityAccess.Infrastructure;
using TenantFlow.ImportsExports.Infrastructure;
using TenantFlow.ImportsExports.Infrastructure.Persistence;
using TenantFlow.Inventory.Infrastructure;
using TenantFlow.Orders.Infrastructure;
using TenantFlow.Orders.Infrastructure.Persistence;
using TenantFlow.Payments.Application.Abstractions;
using TenantFlow.Payments.Infrastructure;
using TenantFlow.Payments.Infrastructure.Persistence;
using TenantFlow.Subscriptions.Infrastructure;
using TenantFlow.Subscriptions.Infrastructure.Persistence;
using TenantFlow.Tenants.Infrastructure;

namespace TenantFlow.Database.Tests.Support;

/// <summary>
/// The Worker's background modules wired the way <c>MessagingExtensions</c> wires them (service tenant context, the modules,
/// the three timer stores, the timer runner; the test runs the timers instead of Quartz), against one test database, with a clock the test moves. The API writes timers on
/// the real clock; the test runs them at "now plus" a deadline.
/// </summary>
internal sealed class WorkerHarness : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private WorkerHarness(ServiceProvider provider, FakeTimeProvider clock)
    {
        _provider = provider;
        Clock = clock;
    }

    public FakeTimeProvider Clock { get; }

    public IServiceProvider Services => _provider;

    /// <param name="database">The test database the modules use.</param>
    /// <param name="wrapProvider">Wraps every payment provider adapter, for example to count calls.</param>
    /// <param name="settings">Configuration the modules read, for example <c>Payments:Provider</c>.</param>
    public static WorkerHarness Create(TestDatabase database, Func<IPaymentProvider, IPaymentProvider>? wrapProvider = null, IReadOnlyDictionary<string, string?>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var connection = database.AppConnectionString;
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<TimeProvider>(clock);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Payments:Simulator:Enabled"] = "true", ["Storage:Provider"] = "filesystem", ["Storage:Filesystem:Enabled"] = "true", ["Storage:Root"] = ApiFactory.StorageRoot }.Concat(settings ?? new Dictionary<string, string?>()))
            .Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddServiceTenantContext();
        services.AddTenantsModule(connection);
        services.AddCatalogModule(connection);
        services.AddInventoryModule(connection);
        services.AddOrdersModule(connection);
        services.AddPaymentsModule(connection);
        services.AddSubscriptionsModule(connection);
        services.AddMembershipDirectory(connection);
        services.AddImportsExportsModule(connection);
        if (wrapProvider is not null)
        {
            var original = services.Single(static d => d.ServiceType == typeof(PaymentProviders));
            services.Remove(original);
            services.AddSingleton(provider =>
            {
                var adapters = (PaymentProviders)original.ImplementationFactory!(provider);
                return new PaymentProviders(adapters.All.Select(wrapProvider), adapters.Current.Name);
            });
        }

        services.AddSingleton(_ => NpgsqlDataSource.Create(connection));
        services.AddSingleton(static provider => new WorkflowTimerStore(provider.GetRequiredService<NpgsqlDataSource>(), OrdersDbContext.Schema));
        services.AddSingleton(static provider => new WorkflowTimerStore(provider.GetRequiredService<NpgsqlDataSource>(), PaymentsDbContext.Schema));
        services.AddSingleton(static provider => new WorkflowTimerStore(provider.GetRequiredService<NpgsqlDataSource>(), SubscriptionsDbContext.Schema));
        services.AddSingleton(static provider => new WorkflowTimerStore(provider.GetRequiredService<NpgsqlDataSource>(), ImportsExportsDbContext.Schema));
        services.AddSingleton(static _ => new WorkflowTimerHandlers(
            static (scope, tenantId) => scope.GetRequiredService<ServiceTenantContext>().Enter(tenantId),
            [.. PaymentsModule.TimerHandlers, .. OrdersModule.TimerHandlers, .. SubscriptionsModule.TimerHandlers, .. ImportsExportsModule.TimerHandlers]));
        services.AddSingleton<WorkflowTimerRunner>();
        return new WorkerHarness(services.BuildServiceProvider(validateScopes: true), clock);
    }

    /// <summary>Moves the clock to <paramref name="later"/> from the real now and runs every due timer until none is left.</summary>
    public async Task RunTimersAfterAsync(TimeSpan later, DateTimeOffset since)
    {
        Clock.SetUtcNow(since + later);
        var runner = _provider.GetRequiredService<WorkflowTimerRunner>();
        while (await runner.RunDueAsync(TestContext.Current.CancellationToken) > 0)
        {
        }
    }

    /// <summary>Runs <paramref name="action"/> in a scope that acts for <paramref name="tenant"/>, as a timer would.</summary>
    public async Task<T> InTenantAsync<T>(Guid tenant, Func<IServiceProvider, Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using var scope = _provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ServiceTenantContext>().Enter(tenant);
        return await action(scope.ServiceProvider);
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}
