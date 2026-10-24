using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TenantFlow.Tenants.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the context. Generating a migration never connects,
/// so the connection string names no server and holds no credential.
/// </summary>
internal sealed class TenantsDesignTimeFactory : IDesignTimeDbContextFactory<TenantsDbContext>
{
    public TenantsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TenantsDbContext>();
        TenantsPersistence.Configure(options, "Host=design-time-only");
        return new TenantsDbContext(options.Options);
    }
}
