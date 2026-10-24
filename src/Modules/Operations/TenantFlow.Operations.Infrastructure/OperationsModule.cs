using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenantFlow.Operations.Application.Abstractions;
using TenantFlow.Operations.Application.Observability;
using TenantFlow.Operations.Application.SupportGrants;
using TenantFlow.Operations.Infrastructure.Persistence;

namespace TenantFlow.Operations.Infrastructure;

/// <summary>The Operations module's single registration entry point for hosts (ADR-0002, ADR-0013).</summary>
public static class OperationsModule
{
    /// <summary>
    /// Support grants and the support views (ADR-0027). The host must also register the Tenants, IdentityAccess and
    /// ImportsExports modules: Operations reaches them through their contracts only.
    /// </summary>
    public static IServiceCollection AddOperationsModule(this IServiceCollection services, string connectionString)
    {
        services.AddOperationsPersistence(connectionString);
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ISupportGrantStore, SupportGrantStore>();
        services.TryAddSingleton<OperationsMetrics>();
        services.AddScoped<SupportService>();
        return services;
    }
}
