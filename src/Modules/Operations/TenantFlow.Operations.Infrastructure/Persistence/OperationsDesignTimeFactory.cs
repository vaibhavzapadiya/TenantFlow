using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TenantFlow.Operations.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the context. Generating a migration never connects,
/// so the connection string names no server and holds no credential.
/// </summary>
internal sealed class OperationsDesignTimeFactory : IDesignTimeDbContextFactory<OperationsDbContext>
{
    public OperationsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OperationsDbContext>();
        OperationsPersistence.Configure(options, "Host=design-time-only");
        return new OperationsDbContext(options.Options);
    }
}
