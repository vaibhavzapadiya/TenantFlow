using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TenantFlow.Orders.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the context. Generating a migration never connects,
/// so the connection string names no server and holds no credential.
/// </summary>
internal sealed class OrdersDesignTimeFactory : IDesignTimeDbContextFactory<OrdersDbContext>
{
    public OrdersDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OrdersDbContext>();
        OrdersPersistence.Configure(options, "Host=design-time-only");
        return new OrdersDbContext(options.Options);
    }
}
