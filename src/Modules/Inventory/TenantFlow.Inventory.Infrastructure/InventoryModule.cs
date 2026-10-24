using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenantFlow.Inventory.Application.Abstractions;
using TenantFlow.Inventory.Application.Stock.Commands;
using TenantFlow.Inventory.Application.Stock.Services;
using TenantFlow.Inventory.Application.Stock.Validators;
using TenantFlow.Inventory.Contracts;
using TenantFlow.Inventory.Infrastructure.Persistence;

namespace TenantFlow.Inventory.Infrastructure;

/// <summary>The Inventory module's single registration entry point for hosts (ADR-0002, ADR-0013).</summary>
public static class InventoryModule
{
    /// <summary>
    /// Use cases, their validator (ADR-0014), and persistence, for hosts that serve requests. The host must also register
    /// the IdentityAccess module (tenant context) and the Catalog module (product existence through its contract).
    /// Other modules reach stock through <see cref="IStockReservations"/> only.
    /// </summary>
    public static IServiceCollection AddInventoryModule(this IServiceCollection services, string connectionString)
    {
        services.AddInventoryPersistence(connectionString);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IValidator<AdjustStockCommand>, AdjustStockCommandValidator>();
        services.AddScoped<IInventoryStore, InventoryStore>();
        services.AddScoped<StockService>();
        services.AddScoped<IStockReservations, StockReservations>();
        return services;
    }
}
