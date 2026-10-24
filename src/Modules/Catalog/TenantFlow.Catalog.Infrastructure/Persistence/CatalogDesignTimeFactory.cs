using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TenantFlow.Catalog.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the context. Generating a migration never connects,
/// so the connection string names no server and holds no credential.
/// </summary>
internal sealed class CatalogDesignTimeFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>();
        CatalogPersistence.Configure(options, "Host=design-time-only");
        return new CatalogDbContext(options.Options);
    }
}
