using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TenantFlow.BuildingBlocks.Abstractions.Workflow;
using TenantFlow.BuildingBlocks.Messaging.Timers;
using TenantFlow.Payments.Application.Abstractions;
using TenantFlow.Payments.Application.Observability;
using TenantFlow.Payments.Application.Payments.Commands;
using TenantFlow.Payments.Application.Payments.Services;
using TenantFlow.Payments.Application.Payments.Validators;
using TenantFlow.Payments.Application.Refunds.Commands;
using TenantFlow.Payments.Application.Refunds.Services;
using TenantFlow.Payments.Application.Refunds.Validators;
using TenantFlow.Payments.Contracts;
using TenantFlow.Payments.Infrastructure.Observability;
using TenantFlow.Payments.Infrastructure.Persistence;
using TenantFlow.Payments.Infrastructure.Providers;
using TenantFlow.Payments.Infrastructure.Providers.Simulator;
using TenantFlow.Payments.Infrastructure.Providers.Stripe;

namespace TenantFlow.Payments.Infrastructure;

/// <summary>The Payments module's single registration entry point for hosts (ADR-0002, ADR-0013).</summary>
public static class PaymentsModule
{
    /// <summary>
    /// Use cases, their validator (ADR-0014), persistence and the payment provider, for hosts that serve requests. The host
    /// must also register the IdentityAccess module (tenant context) and the Orders module (the order to pay): Payments
    /// reaches them through their contracts only, and the Tenants module (whether a webhook's tenant exists). New payments
    /// go to <c>Payments:Provider</c>: the deterministic simulator (default) or Stripe in test mode (ADR-0022 decision 1),
    /// whose secret key must be a test key or the host refuses to start. Webhooks of both are accepted, each verified with
    /// its own secret (<c>Payments:Simulator:WebhookSecret</c>, <c>Payments:Stripe:WebhookSecret</c>).
    /// </summary>
    public static IServiceCollection AddPaymentsModule(this IServiceCollection services, string connectionString)
    {
        services.AddPaymentsPersistence(connectionString);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IValidator<CreatePaymentCommand>, CreatePaymentCommandValidator>();
        services.AddOptions<SimulatorOptions>().Configure<IConfiguration>(static (options, configuration) =>
        {
            options.WebhookSecret = configuration[$"{SimulatorOptions.SectionName}:{nameof(SimulatorOptions.WebhookSecret)}"];
            options.Enabled = string.Equals(configuration[$"{SimulatorOptions.SectionName}:{nameof(SimulatorOptions.Enabled)}"], "true", StringComparison.OrdinalIgnoreCase);
        });
        services.AddOptions<StripeOptions>().Configure<IConfiguration>(static (options, configuration) =>
        {
            options.SecretKey = configuration[$"{StripeOptions.SectionName}:{nameof(StripeOptions.SecretKey)}"];
            options.WebhookSecret = configuration[$"{StripeOptions.SectionName}:{nameof(StripeOptions.WebhookSecret)}"];
        });
        // Messages name the key, never a value (ADR-0008).
        services.AddOptions<PaymentProviderOptions>()
            .Configure<IConfiguration>(static (options, configuration) =>
                options.Provider = configuration[$"{PaymentProviderOptions.SectionName}:{nameof(PaymentProviderOptions.Provider)}"] ?? options.Provider)
            .Validate(static o => o.Provider is SimulatedPaymentProvider.ProviderName or StripePaymentProvider.ProviderName,
                "Payments:Provider must be 'simulator' or 'stripe'.")
            .Validate<IOptions<StripeOptions>>(static (o, stripe) => o.Provider != StripePaymentProvider.ProviderName
                    || (stripe.Value.SecretKey?.StartsWith(StripeOptions.TestKeyPrefix, StringComparison.Ordinal) ?? false),
                "Payments:Stripe:SecretKey must be a Stripe test-mode key (sk_test_...) when Payments:Provider is 'stripe'.")
            // The simulator charges nothing: it may take payments only where it is switched on (Development, tests).
            .Validate<IOptions<SimulatorOptions>>(static (o, simulator) => o.Provider != SimulatedPaymentProvider.ProviderName || simulator.Value.Enabled,
                "Payments:Simulator:Enabled must be true to take payments with the simulator; set Payments:Provider otherwise.")
            .ValidateOnStart();
        services.AddSingleton<SimulatedPaymentProvider>();
        services.AddSingleton(static provider => StripeTransport.Create(provider.GetRequiredService<IOptions<StripeOptions>>().Value));
        services.AddSingleton<StripePaymentProvider>();
        // Every adapter stays reachable, so a payment is always settled by the provider that took it. Each call is timed.
        services.TryAddSingleton<PaymentsMetrics>();
        services.AddScoped<UnresolvedPaymentMetrics>();
        services.AddSingleton(static provider => new PaymentProviders(
            [Measured(provider, provider.GetRequiredService<SimulatedPaymentProvider>()), Measured(provider, provider.GetRequiredService<StripePaymentProvider>())],
            provider.GetRequiredService<IOptions<PaymentProviderOptions>>().Value.Provider));
        services.AddSingleton<IPaymentWebhookReader, SimulatedWebhookReader>();
        services.AddSingleton<IPaymentWebhookReader, StripeWebhookReader>();
        services.AddScoped<PaymentWebhookService>();
        services.AddScoped<IPaymentStore, PaymentStore>();
        services.AddScoped<PaymentService>();
        services.AddScoped<PaymentReconciler>();
        services.AddScoped<ISubscriptionCharges, SubscriptionCharges>();
        services.AddSingleton<IValidator<CreateRefundCommand>, CreateRefundCommandValidator>();
        services.AddScoped<RefundSender>();
        services.AddScoped<RemainderRefunds>();
        services.AddScoped<RefundService>();
        services.AddScoped<RefundReconciler>();
        services.AddScoped<IOrderRefunds, OrderRefunds>();
        return services;
    }

    /// <summary>The module's recurring tasks, for the Worker's scheduler (SPEC-0010 criterion 4).</summary>
    public static IEnumerable<ScheduledTask> ScheduledTasks =>
    [
        new(UnresolvedPaymentMetrics.TaskName, UnresolvedPaymentMetrics.CronExpression, static (services, cancellationToken) =>
            services.GetRequiredService<UnresolvedPaymentMetrics>().RefreshAsync(cancellationToken)),
    ];

    /// <summary>The module's workflow timers, for the Worker's timer runner (ADR-0022 decision 7).</summary>
    public static IEnumerable<KeyValuePair<string, TimerHandler>> TimerHandlers =>
    [
        new(PaymentReconciliation.TimerKind, static (services, timer, cancellationToken) =>
            services.GetRequiredService<PaymentReconciler>().ReconcileAsync(timer.SubjectId, timer.Attempts, cancellationToken)),
        new(RefundReconciliation.TimerKind, static (services, timer, cancellationToken) =>
            services.GetRequiredService<RefundReconciler>().ReconcileAsync(timer.SubjectId, timer.Attempts, cancellationToken)),
    ];

    private static MeasuredPaymentProvider Measured(IServiceProvider services, IPaymentProvider adapter) =>
        new(adapter, services.GetRequiredService<PaymentsMetrics>());
}
