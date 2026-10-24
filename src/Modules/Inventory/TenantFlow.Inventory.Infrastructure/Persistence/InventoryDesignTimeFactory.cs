using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TenantFlow.Inventory.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the context. Generating a migration never connects,
/// so the connection string names no server and holds no credential.
/// </summary>
internal sealed class InventoryDesignTimeFactory : IDesignTimeDbContextFactory<InventoryDbContext>
{
    public InventoryDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>();
        InventoryPersistence.Configure(options, "Host=design-time-only");
        return new InventoryDbContext(options.Options);
    }
}
