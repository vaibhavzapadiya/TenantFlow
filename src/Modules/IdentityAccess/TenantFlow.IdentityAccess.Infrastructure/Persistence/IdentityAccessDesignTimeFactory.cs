using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TenantFlow.IdentityAccess.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the context. Generating a migration never connects,
/// so the connection string names no server and holds no credential.
/// </summary>
internal sealed class IdentityAccessDesignTimeFactory : IDesignTimeDbContextFactory<IdentityAccessDbContext>
{
    public IdentityAccessDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<IdentityAccessDbContext>();
        IdentityAccessPersistence.Configure(options, "Host=design-time-only");
        return new IdentityAccessDbContext(options.Options);
    }
}
