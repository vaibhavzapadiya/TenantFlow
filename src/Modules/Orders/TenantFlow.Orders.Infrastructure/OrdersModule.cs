using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenantFlow.BuildingBlocks.Messaging.Timers;
using TenantFlow.Orders.Application.Abstractions;
using TenantFlow.Orders.Application.Observability;
using TenantFlow.Orders.Application.Orders.Commands;
using TenantFlow.Orders.Application.Orders.Services;
using TenantFlow.Orders.Application.Orders.Validators;
using TenantFlow.Orders.Contracts;
using TenantFlow.Orders.Infrastructure.Persistence;

namespace TenantFlow.Orders.Infrastructure;

/// <summary>The Orders module's single registration entry point for hosts (ADR-0002, ADR-0013).</summary>
public static class OrdersModule
{
    /// <summary>
    /// Use cases, their validator (ADR-0014), and persistence, for hosts that serve requests. The host must also register
    /// the IdentityAccess module (tenant context), the Catalog module (product snapshots) and the Inventory module (stock
    /// reservations): Orders reaches them through their contracts only. The Payments module reaches Orders through
    /// <see cref="IOrderPayments"/>; a host that runs the order timers also needs the Payments module (cancellation refunds).
    /// </summary>
    public static IServiceCollection AddOrdersModule(this IServiceCollection services, string connectionString)
    {
        services.AddOrdersPersistence(connectionString);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IValidator<OrderLinesCommand>, OrderLinesCommandValidator>();
        services.AddScoped<IOrderStore, OrderStore>();
        services.AddScoped<OrderService>();
        services.AddScoped<IOrderPayments, OrderPayments>();
        services.AddScoped<PaymentEventConsumer>();
        services.TryAddSingleton<OrdersMetrics>();
        services.AddScoped<OrderReservationStep>();
        services.AddScoped<OrderRecovery>();
        services.AddScoped<OrderCancellationRefunds>();
        return services;
    }

    /// <summary>The module's workflow timers, for the Worker's timer runner (ADR-0022 decision 7).</summary>
    public static IEnumerable<KeyValuePair<string, TimerHandler>> TimerHandlers =>
    [
        new(OrderDeadlines.ResumeSubmitKind, static (services, timer, cancellationToken) =>
            services.GetRequiredService<OrderRecovery>().ResumeSubmitAsync(timer.SubjectId, cancellationToken)),
        new(OrderDeadlines.ExpireUnpaidKind, static (services, timer, cancellationToken) =>
            services.GetRequiredService<OrderRecovery>().ExpireUnpaidAsync(timer.SubjectId, cancellationToken)),
        new(OrderDeadlines.RequestCancellationRefundKind, static (services, timer, cancellationToken) =>
            services.GetRequiredService<OrderCancellationRefunds>().RequestAsync(timer.SubjectId, cancellationToken)),
    ];
}
