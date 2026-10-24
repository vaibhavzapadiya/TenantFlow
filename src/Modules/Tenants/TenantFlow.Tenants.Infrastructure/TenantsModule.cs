using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenantFlow.Tenants.Application.Abstractions;
using TenantFlow.Tenants.Application.Lifecycle.Commands;
using TenantFlow.Tenants.Application.Lifecycle.Services;
using TenantFlow.Tenants.Application.Lifecycle.Validators;
using TenantFlow.Tenants.Contracts;
using TenantFlow.Tenants.Infrastructure.Persistence;

namespace TenantFlow.Tenants.Infrastructure;

/// <summary>The Tenants module's single registration entry point for hosts (ADR-0002, ADR-0013).</summary>
public static class TenantsModule
{
    /// <summary>Use cases, their validator (ADR-0014), and persistence, for hosts that serve requests.</summary>
    public static IServiceCollection AddTenantsModule(this IServiceCollection services, string connectionString)
    {
        services.AddTenantsPersistence(connectionString);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IValidator<CreateTenantCommand>, CreateTenantCommandValidator>();
        services.AddScoped<ITenantStore, TenantStore>();
        services.AddScoped<TenantLifecycleService>();
        // The module's public surface for other modules (ADR-0002).
        services.AddScoped<ITenantDirectory, TenantDirectory>();
        return services;
    }
}
