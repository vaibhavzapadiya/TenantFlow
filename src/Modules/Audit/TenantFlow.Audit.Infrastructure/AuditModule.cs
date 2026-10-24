using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenantFlow.Audit.Application.Abstractions;
using TenantFlow.Audit.Application.Records.Services;
using TenantFlow.Audit.Infrastructure.Persistence;

namespace TenantFlow.Audit.Infrastructure;

/// <summary>The Audit module's single registration entry point for hosts (ADR-0002, ADR-0013).</summary>
public static class AuditModule
{
    /// <summary>
    /// The audit-event consumer, the audit log reads and persistence (ADR-0026). The host must also register the Tenants
    /// module: Audit checks that a tenant exists through its contract only.
    /// </summary>
    public static IServiceCollection AddAuditModule(this IServiceCollection services, string connectionString)
    {
        services.AddAuditPersistence(connectionString);
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IAuditStore, AuditStore>();
        services.AddScoped<AuditEventConsumer>();
        services.AddScoped<AuditLog>();
        return services;
    }
}
