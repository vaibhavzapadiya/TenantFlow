using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TenantFlow.Subscriptions.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the context. Generating a migration never connects,
/// so the connection string names no server and holds no credential.
/// </summary>
internal sealed class SubscriptionsDesignTimeFactory : IDesignTimeDbContextFactory<SubscriptionsDbContext>
{
    public SubscriptionsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SubscriptionsDbContext>();
        SubscriptionsPersistence.Configure(options, "Host=design-time-only");
        return new SubscriptionsDbContext(options.Options);
    }
}
