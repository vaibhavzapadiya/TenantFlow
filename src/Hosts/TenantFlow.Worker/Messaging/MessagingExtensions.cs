using Npgsql;
using TenantFlow.Audit.Application.Records.Services;
using TenantFlow.Audit.Infrastructure;
using TenantFlow.BuildingBlocks.Messaging.Outbox;
using TenantFlow.BuildingBlocks.Messaging.RabbitMq;
using TenantFlow.BuildingBlocks.Messaging.Timers;
using TenantFlow.BuildingBlocks.Observability;
using TenantFlow.BuildingBlocks.Scheduling;
using TenantFlow.Catalog.Infrastructure;
using TenantFlow.IdentityAccess.Application.TenantAccess;
using TenantFlow.IdentityAccess.Infrastructure;
using TenantFlow.IdentityAccess.Infrastructure.Persistence;
using TenantFlow.ImportsExports.Infrastructure;
using TenantFlow.ImportsExports.Infrastructure.Persistence;
using TenantFlow.Inventory.Infrastructure;
using TenantFlow.Notifications.Application.Notifications.Services;
using TenantFlow.Notifications.Infrastructure;
using TenantFlow.Operations.Infrastructure.Persistence;
using TenantFlow.Orders.Application.Orders.Services;
using TenantFlow.Orders.Infrastructure;
using TenantFlow.Orders.Infrastructure.Persistence;
using TenantFlow.Payments.Infrastructure;
using TenantFlow.Payments.Infrastructure.Persistence;
using TenantFlow.Subscriptions.Infrastructure;
using TenantFlow.Subscriptions.Infrastructure.Persistence;
using TenantFlow.Tenants.Infrastructure;
using TenantFlow.Tenants.Infrastructure.Persistence;
using TenantFlow.Worker.Health;
using TenantFlow.Worker.Retention;

namespace TenantFlow.Worker.Messaging;

/// <summary>
/// The worker's database, broker, outbox publisher and consumers (ADR-0021), with readiness checks for both dependencies.
/// </summary>
internal static class MessagingExtensions
{
    public static WebApplicationBuilder AddWorkerMessaging(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        // Required configuration is validated before the host serves anything.
        var connectionString = services.AddTenantFlowDatabase(builder.Configuration);
        services.AddOptions<RabbitMqOptions>()
            .BindConfiguration(RabbitMqOptions.SectionName)
            .PostConfigure(static options => options.ClientName = "tenantflow-worker")
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<OutboxOptions>()
            .BindConfiguration(OutboxOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<RabbitMqConnection>();
        services.AddSingleton<IMessageBus, RabbitMqMessageBus>();

        // One store per publishing module; a module that starts publishing adds its schema here.
        services.AddSingleton(static provider => new OutboxStore(provider.GetRequiredService<NpgsqlDataSource>(), OrdersDbContext.Schema));
        services.AddSingleton(static provider => new OutboxStore(provider.GetRequiredService<NpgsqlDataSource>(), PaymentsDbContext.Schema));
        // Tenants, IdentityAccess, ImportsExports and Operations publish their audit events (ADR-0026).
        services.AddSingleton(static provider => new OutboxStore(provider.GetRequiredService<NpgsqlDataSource>(), TenantsDbContext.Schema));
        services.AddSingleton(static provider => new OutboxStore(provider.GetRequiredService<NpgsqlDataSource>(), IdentityAccessDbContext.Schema));
        services.AddSingleton(static provider => new OutboxStore(provider.GetRequiredService<NpgsqlDataSource>(), ImportsExportsDbContext.Schema));
        services.AddSingleton(static provider => new OutboxStore(provider.GetRequiredService<NpgsqlDataSource>(), OperationsDbContext.Schema));
        services.AddSingleton<OutboxMetrics>();
        services.AddSingleton<OutboxPublisher>();
        services.AddHostedService<OutboxPublisherService>();

        // Consumers: Notifications records order events; Tenants answers whether a message's tenant exists.
        services.AddTenantsModule(connectionString);
        services.AddNotificationsModule(connectionString);
        ConsumerHostedService.Add(services, OrderEventConsumer.Consumer, OrderEventConsumer.EventTypes, static (scope, message, ct) =>
            scope.GetRequiredService<OrderEventConsumer>().HandleAsync(message, ct));
        // Audit records every module's audit events in the append-only log (SPEC-0009, ADR-0026).
        services.AddAuditModule(connectionString);
        ConsumerHostedService.Add(services, AuditEventConsumer.Consumer, AuditEventConsumer.EventTypes, static (scope, message, ct) =>
            scope.GetRequiredService<AuditEventConsumer>().HandleAsync(message, ct));

        // The order payment saga (ADR-0022): Orders consumes payment events and releases stock through Inventory, in the
        // message's tenant, entered per message into a service tenant context that holds no membership or permission.
        services.AddServiceTenantContext();
        services.AddCatalogModule(connectionString);
        services.AddInventoryModule(connectionString);
        services.AddOrdersModule(connectionString);
        ConsumerHostedService.Add(services, PaymentEventConsumer.Consumer, PaymentEventConsumer.EventTypes, static (scope, message, ct) =>
        {
            // The saga runs in the message's tenant: its validated envelope enters the scope's service tenant context.
            scope.GetRequiredService<ServiceTenantContext>().Enter(message.TenantId);
            return scope.GetRequiredService<PaymentEventConsumer>().HandleAsync(message, ct);
        });

        // Workflow timers (ADR-0022 decision 7): payment reconciliation, order deadlines and subscription renewals, one
        // store per module that keeps due rows, each run in a scope entered for the row's tenant. Quartz.NET fires one job
        // per module every minute, clustered across Worker instances (ADR-0023 decision 1).
        services.AddPaymentsModule(connectionString);
        services.AddSubscriptionsModule(connectionString);
        services.AddMembershipDirectory(connectionString);
        services.AddImportsExportsModule(connectionString);
        string[] timerSchemas = [OrdersDbContext.Schema, PaymentsDbContext.Schema, SubscriptionsDbContext.Schema, ImportsExportsDbContext.Schema];
        foreach (var schema in timerSchemas)
        {
            services.AddSingleton(provider => new WorkflowTimerStore(provider.GetRequiredService<NpgsqlDataSource>(), schema));
        }

        services.AddSingleton(static _ => new WorkflowTimerHandlers(
            static (scope, tenantId) => scope.GetRequiredService<ServiceTenantContext>().Enter(tenantId),
            [.. PaymentsModule.TimerHandlers, .. OrdersModule.TimerHandlers, .. SubscriptionsModule.TimerHandlers, .. ImportsExportsModule.TimerHandlers]));
        services.AddSingleton<WorkflowTimerRunner>();
        // Recurring tasks: the imports and exports and the payments metrics (SPEC-0008 criterion 11, SPEC-0010 criterion 4)
        // and the daily retention run, which enters each tenant's scope itself (SPEC-0009 criteria 8 to 10).
        services.AddTenantFlowScheduler(connectionString, timerSchemas, SchedulerRegistration.DefaultInterval, [.. ImportsExportsModule.ScheduledTasks, .. PaymentsModule.ScheduledTasks, RetentionTask.Task]);

        services.AddHealthChecks()
            .AddCheck<RabbitMqReadinessCheck>("rabbitmq", tags: [HealthEndpoints.ReadyTag]);
        return builder;
    }
}
