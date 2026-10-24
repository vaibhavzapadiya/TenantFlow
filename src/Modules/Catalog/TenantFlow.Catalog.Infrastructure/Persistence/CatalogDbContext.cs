using Microsoft.EntityFrameworkCore;
using TenantFlow.Catalog.Domain.Products;
using TenantFlow.Catalog.Infrastructure.Persistence.Configurations;

namespace TenantFlow.Catalog.Infrastructure.Persistence;

/// <summary>
/// The Catalog module's database context. It maps only to the <c>catalog</c> schema, and no other
/// module may use it (ADR-0002, ADR-0012). Only the Migrator applies its migrations.
/// </summary>
public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public const string Schema = "catalog";

    internal DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new ProductConfiguration());
    }
}
