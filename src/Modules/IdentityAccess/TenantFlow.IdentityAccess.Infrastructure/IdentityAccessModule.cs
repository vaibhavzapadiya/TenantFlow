using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenantFlow.IdentityAccess.Application.Abstractions;
using TenantFlow.IdentityAccess.Application.Memberships.Commands;
using TenantFlow.IdentityAccess.Application.Memberships.Services;
using TenantFlow.IdentityAccess.Application.Memberships.Validators;
using TenantFlow.IdentityAccess.Application.TenantAccess;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.IdentityAccess.Infrastructure.Persistence;

namespace TenantFlow.IdentityAccess.Infrastructure;

/// <summary>The IdentityAccess module's single registration entry point for hosts (ADR-0002, ADR-0013).</summary>
public static class IdentityAccessModule
{
    /// <summary>
    /// Use cases, their validators (ADR-0014), and persistence, for hosts that serve requests. The host must also
    /// register the Tenants module: membership listing asks its <c>ITenantDirectory</c>.
    /// </summary>
    public static IServiceCollection AddIdentityAccessModule(this IServiceCollection services, string connectionString)
    {
        services.AddIdentityAccessPersistence(connectionString);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IValidator<AddMemberCommand>, AddMemberCommandValidator>();
        services.AddSingleton<IValidator<ChangeRoleCommand>, ChangeRoleCommandValidator>();
        services.AddScoped<IMembershipStore, MembershipStore>();
        services.AddScoped<MembershipService>();
        // The resolver is the tenant context for its scope: one instance, so what it resolves is what code reads.
        services.AddScoped<TenantAccessResolver>();
        services.AddScoped<ITenantContext>(static provider => provider.GetRequiredService<TenantAccessResolver>());
        services.AddScoped<TenantMembersService>();
        // The read-only member view of support grants (ADR-0027): no issuer or subject leaves the module.
        services.AddScoped<ISupportMemberView, SupportMemberView>();
        services.TryAddScoped<IMembershipDirectory, MembershipDirectory>();
        return services;
    }

    /// <summary>
    /// The tenant context of background work, for the Worker instead of <see cref="AddIdentityAccessModule"/>: one
    /// <see cref="ServiceTenantContext"/> per scope, entered from a validated message or timer (ADR-0022). It never
    /// resolves a membership, so it grants no permission.
    /// </summary>
    public static IServiceCollection AddServiceTenantContext(this IServiceCollection services)
    {
        services.AddScoped<ServiceTenantContext>();
        services.AddScoped<ITenantContext>(static provider => provider.GetRequiredService<ServiceTenantContext>());
        return services;
    }

    /// <summary>
    /// Whether a membership of the current tenant is still active (<see cref="IMembershipDirectory"/>), for background work
    /// that acts for a member, such as subscription renewals in the Worker (SPEC-0007 review fix R9).
    /// </summary>
    public static IServiceCollection AddMembershipDirectory(this IServiceCollection services, string connectionString)
    {
        services.AddIdentityAccessPersistence(connectionString);
        services.TryAddScoped<IMembershipDirectory, MembershipDirectory>();
        return services;
    }
}
