using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenantFlow.Catalog.Application.Abstractions;
using TenantFlow.Catalog.Application.Products.Commands;
using TenantFlow.Catalog.Application.Products.Services;
using TenantFlow.Catalog.Application.Products.Validators;
using TenantFlow.Catalog.Contracts;
using TenantFlow.Catalog.Infrastructure.Persistence;

namespace TenantFlow.Catalog.Infrastructure;

/// <summary>The Catalog module's single registration entry point for hosts (ADR-0002, ADR-0013).</summary>
public static class CatalogModule
{
    /// <summary>
    /// Use cases, their validators (ADR-0014), and persistence, for hosts that serve requests. The host must also
    /// register the IdentityAccess module: the use cases act on its <c>ITenantContext</c>.
    /// </summary>
    public static IServiceCollection AddCatalogModule(this IServiceCollection services, string connectionString)
    {
        services.AddCatalogPersistence(connectionString);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IValidator<CreateProductCommand>, CreateProductCommandValidator>();
        services.AddSingleton<IValidator<UpdateProductCommand>, UpdateProductCommandValidator>();
        services.AddScoped<ICatalogStore, CatalogStore>();
        services.AddScoped<ProductService>();
        // The module's public surface for other modules (ADR-0002).
        services.AddScoped<IProductCatalog, ProductCatalog>();
        services.AddScoped<ICatalogImport, CatalogImport>();
        services.AddScoped<ICatalogExport, CatalogExport>();
        return services;
    }
}
