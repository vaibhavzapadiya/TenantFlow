using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TenantFlow.BuildingBlocks.Scheduling.Persistence;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the context. Generating a migration never connects,
/// so the connection string names no server and holds no credential.
/// </summary>
internal sealed class SchedulerDesignTimeFactory : IDesignTimeDbContextFactory<SchedulerDbContext>
{
    public SchedulerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SchedulerDbContext>();
        SchedulerPersistence.Configure(options, "Host=design-time-only");
        return new SchedulerDbContext(options.Options);
    }
}
