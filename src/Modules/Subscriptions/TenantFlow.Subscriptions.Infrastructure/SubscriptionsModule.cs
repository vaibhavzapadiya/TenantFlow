using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenantFlow.BuildingBlocks.Messaging.Timers;
using TenantFlow.Subscriptions.Application.Abstractions;
using TenantFlow.Subscriptions.Application.Plans.Commands;
using TenantFlow.Subscriptions.Application.Plans.Services;
using TenantFlow.Subscriptions.Application.Plans.Validators;
using TenantFlow.Subscriptions.Application.Subscriptions.Commands;
using TenantFlow.Subscriptions.Application.Subscriptions.Services;
using TenantFlow.Subscriptions.Application.Subscriptions.Validators;
using TenantFlow.Subscriptions.Infrastructure.Persistence;

namespace TenantFlow.Subscriptions.Infrastructure;

/// <summary>The Subscriptions module's single registration entry point for hosts (ADR-0002, ADR-0013).</summary>
public static class SubscriptionsModule
{
    /// <summary>
    /// Use cases, their validators (ADR-0014) and persistence, for hosts that serve requests. The host must also register
    /// the IdentityAccess module (tenant context), which Subscriptions reaches through its contract only; a host that runs
    /// renewals (<see cref="TimerHandlers"/>) also the Payments module, which charges them.
    /// </summary>
    public static IServiceCollection AddSubscriptionsModule(this IServiceCollection services, string connectionString)
    {
        services.AddSubscriptionsPersistence(connectionString);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IValidator<CreatePlanCommand>, CreatePlanCommandValidator>();
        services.AddSingleton<IValidator<SubscribeCommand>, SubscribeCommandValidator>();
        services.AddScoped<ISubscriptionStore, SubscriptionStore>();
        services.AddScoped<PlanService>();
        services.AddScoped<SubscriptionService>();
        services.AddScoped<SubscriptionRenewer>();
        return services;
    }

    /// <summary>The module's due rows, for the Worker's timer runner (ADR-0023 decision 1).</summary>
    public static IEnumerable<KeyValuePair<string, TimerHandler>> TimerHandlers =>
    [
        new(SubscriptionRenewals.TimerKind, static (services, timer, cancellationToken) =>
            services.GetRequiredService<SubscriptionRenewer>().RenewAsync(timer.SubjectId, cancellationToken)),
    ];
}
