using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenantFlow.Notifications.Application.Abstractions;
using TenantFlow.Notifications.Application.Notifications.Services;
using TenantFlow.Notifications.Infrastructure.Persistence;

namespace TenantFlow.Notifications.Infrastructure;

/// <summary>The Notifications module's single registration entry point for hosts (ADR-0002, ADR-0013).</summary>
public static class NotificationsModule
{
    /// <summary>
    /// The order-event consumer and persistence, for hosts that consume messages. The host must also register the Tenants
    /// module: Notifications checks that a message's tenant exists through its contract only.
    /// </summary>
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services, string connectionString)
    {
        services.AddNotificationsPersistence(connectionString);
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<INotificationStore, NotificationStore>();
        services.AddScoped<OrderEventConsumer>();
        return services;
    }
}
