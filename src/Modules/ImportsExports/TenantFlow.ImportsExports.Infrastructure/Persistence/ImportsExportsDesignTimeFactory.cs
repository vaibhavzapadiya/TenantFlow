using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TenantFlow.ImportsExports.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the context. Generating a migration never connects,
/// so the connection string names no server and holds no credential.
/// </summary>
internal sealed class ImportsExportsDesignTimeFactory : IDesignTimeDbContextFactory<ImportsExportsDbContext>
{
    public ImportsExportsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ImportsExportsDbContext>();
        ImportsExportsPersistence.Configure(options, "Host=design-time-only");
        return new ImportsExportsDbContext(options.Options);
    }
}
