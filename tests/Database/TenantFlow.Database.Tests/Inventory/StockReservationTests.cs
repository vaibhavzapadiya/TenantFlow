using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TenantFlow.BuildingBlocks.Persistence;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.Inventory.Contracts;
using TenantFlow.Inventory.Infrastructure;

namespace TenantFlow.Database.Tests.Inventory;

/// <summary>
/// SPEC-0004 criteria 1 to 5 (the contract level) and 14 for reservations, against real PostgreSQL. The contract is
/// called the way Orders will call it: through <see cref="IStockReservations"/>, with a tenant context set. Stock is
/// stocked and read through the real API.
/// </summary>
public sealed class StockReservationTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;

    private WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("Not initialized.");

    public ValueTask InitializeAsync()
    {
        _factory = ApiFactory.CreateProduction(connectionString: fixture.Database.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    private sealed record World(Guid TenantA, Guid TenantB, string AdminA, string AdminB);

    // ---- criterion 1 ----

    [Fact]
    public async Task A_reservation_holds_units_and_the_stock_read_shows_reserved_and_available()
    {
        var w = await CreateWorldAsync();
        var product = await StockedProductAsync(w.TenantA, w.AdminA, 5);

        var outcome = await ReserveAsync(w.TenantA, Guid.CreateVersion7(), (product, 3));

        Assert.Equal(ReservationOutcome.Reserved, outcome);
        Assert.Equal((5L, 3L, 2L), await StockAsync(w.TenantA, product, w.AdminA));
    }

    // ---- criterion 2 ----

    [Fact]
    public async Task A_reservation_is_every_line_or_none()
    {
        var w = await CreateWorldAsync();
        var plenty = await StockedProductAsync(w.TenantA, w.AdminA, 5);
        var scarce = await StockedProductAsync(w.TenantA, w.AdminA, 1);
        var order = Guid.CreateVersion7();

        var outcome = await ReserveAsync(w.TenantA, order, (plenty, 2), (scarce, 2));

        Assert.Equal(ReservationOutcome.InsufficientStock, outcome);
        Assert.Equal((5L, 0L, 5L), await StockAsync(w.TenantA, plenty, w.AdminA));
        Assert.Equal((1L, 0L, 1L), await StockAsync(w.TenantA, scarce, w.AdminA));
        Assert.Equal(0L, await ReservationRowsAsync(w.TenantA, order));
    }

    // ---- criterion 3 ----

    [Fact]
    public async Task Reserving_or_releasing_an_order_twice_counts_once()
    {
        var w = await CreateWorldAsync();
        var product = await StockedProductAsync(w.TenantA, w.AdminA, 5);
        var order = Guid.CreateVersion7();

        await ReserveAsync(w.TenantA, order, (product, 3));
        var again = await ReserveAsync(w.TenantA, order, (product, 3));
        var afterReserves = await StockAsync(w.TenantA, product, w.AdminA);
        await ReleaseAsync(w.TenantA, order);
        await ReleaseAsync(w.TenantA, order);

        Assert.Equal(ReservationOutcome.Reserved, again);
        Assert.Equal((5L, 3L, 2L), afterReserves);
        Assert.Equal((5L, 0L, 5L), await StockAsync(w.TenantA, product, w.AdminA));
    }

    [Fact]
    public async Task The_same_order_reserved_concurrently_holds_its_units_once()
    {
        var w = await CreateWorldAsync();
        var product = await StockedProductAsync(w.TenantA, w.AdminA, 10);
        var order = Guid.CreateVersion7();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => ReserveAsync(w.TenantA, order, (product, 4))));

        Assert.All(outcomes, static o => Assert.Equal(ReservationOutcome.Reserved, o));
        Assert.Equal((10L, 4L, 6L), await StockAsync(w.TenantA, product, w.AdminA));
    }

    [Fact]
    public async Task A_repeat_for_other_lines_or_after_a_release_fails_and_holds_nothing_more()
    {
        var w = await CreateWorldAsync();
        var held = await StockedProductAsync(w.TenantA, w.AdminA, 5);
        var extra = await StockedProductAsync(w.TenantA, w.AdminA, 5);
        var order = Guid.CreateVersion7();
        var released = Guid.CreateVersion7();
        await ReserveAsync(w.TenantA, order, (held, 2));
        await ReserveAsync(w.TenantA, released, (extra, 1));
        await ReleaseAsync(w.TenantA, released);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ReserveAsync(w.TenantA, order, (held, 2), (extra, 1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReserveAsync(w.TenantA, released, (extra, 1)));

        Assert.Equal((5L, 2L, 3L), await StockAsync(w.TenantA, held, w.AdminA));
        Assert.Equal((5L, 0L, 5L), await StockAsync(w.TenantA, extra, w.AdminA));
    }

    // ---- criterion 4 ----

    [Fact]
    public async Task An_adjustment_cannot_take_stock_below_what_is_reserved()
    {
        var w = await CreateWorldAsync();
        var product = await StockedProductAsync(w.TenantA, w.AdminA, 5);
        await ReserveAsync(w.TenantA, Guid.CreateVersion7(), (product, 3));

        using var refused = await AdjustAsync(w.TenantA, product, w.AdminA, -3);
        var afterRefusal = await StockAsync(w.TenantA, product, w.AdminA);
        using var allowed = await AdjustAsync(w.TenantA, product, w.AdminA, -2);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal((5L, 3L, 2L), afterRefusal);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal((3L, 3L, 0L), await StockAsync(w.TenantA, product, w.AdminA));
    }

    // ---- criterion 5, at the contract level: twenty orders, one last unit ----

    [Fact]
    public async Task Twenty_concurrent_orders_for_the_last_unit_reserve_it_exactly_once()
    {
        var w = await CreateWorldAsync();
        var product = await StockedProductAsync(w.TenantA, w.AdminA, 1);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => ReserveAsync(w.TenantA, Guid.CreateVersion7(), (product, 1))));

        Assert.Equal(1, outcomes.Count(static o => o == ReservationOutcome.Reserved));
        Assert.Equal(19, outcomes.Count(static o => o == ReservationOutcome.InsufficientStock));
        Assert.Equal((1L, 1L, 0L), await StockAsync(w.TenantA, product, w.AdminA));
    }

    // ---- criterion 14: the cross-tenant denial ----

    [Fact]
    public async Task Another_tenants_stock_cannot_be_reserved_or_released()
    {
        var w = await CreateWorldAsync();
        var bProduct = await StockedProductAsync(w.TenantB, w.AdminB, 5);
        var bOrder = Guid.CreateVersion7();
        await ReserveAsync(w.TenantB, bOrder, (bProduct, 2));

        // Tenant A's context names tenant B's product and tenant B's order.
        var outcome = await ReserveAsync(w.TenantA, Guid.CreateVersion7(), (bProduct, 1));
        await ReleaseAsync(w.TenantA, bOrder);

        Assert.Equal(ReservationOutcome.InsufficientStock, outcome);
        Assert.Equal((5L, 2L, 3L), await StockAsync(w.TenantB, bProduct, w.AdminB));
        Assert.Equal(1L, await ReservationRowsAsync(w.TenantB, bOrder));
    }

    // ---- helpers ----

    private async Task<ReservationOutcome> ReserveAsync(Guid tenant, Guid order, params (Guid Product, int Quantity)[] lines)
    {
        await using var services = ContractServices(tenant);
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IStockReservations>()
            .ReserveAsync(order, [.. lines.Select(static l => new ReservationLine(l.Product, l.Quantity))], TestContext.Current.CancellationToken);
    }

    private async Task ReleaseAsync(Guid tenant, Guid order)
    {
        await using var services = ContractServices(tenant);
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IStockReservations>().ReleaseAsync(order, TestContext.Current.CancellationToken);
    }

    // The Inventory module as a host registers it, with a tenant context in place of a resolved membership.
    private ServiceProvider ContractServices(Guid tenant)
    {
        var services = new ServiceCollection();
        services.AddInventoryModule(fixture.Database.AppConnectionString);
        services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenant));
        return services.BuildServiceProvider();
    }

    private async Task<World> CreateWorldAsync()
    {
        var harness = fixture.Harness;
        var suffix = Guid.NewGuid().ToString("N");
        var a = await harness.CreateTenantAsync("Tenant A");
        var b = await harness.CreateTenantAsync("Tenant B");
        var world = new World(a, b, $"admin-a-{suffix}", $"admin-b-{suffix}");
        await harness.AddAsync(a, world.AdminA, "tenant-admin", TestIssuer.Issuer);
        await harness.AddAsync(b, world.AdminB, "tenant-admin", TestIssuer.Issuer);
        return world;
    }

    private async Task<Guid> StockedProductAsync(Guid tenant, string admin, int onHand)
    {
        using var created = await SendAsync(HttpMethod.Post, $"/v1/tenants/{tenant}/products", admin,
            new { sku = $"SKU-{Guid.NewGuid():N}", name = "Widget", price = new { amount = 1m, currency = "EUR" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var product = Guid.Parse(created.Headers.Location!.ToString().Split('/')[^1]);
        using var stocked = await AdjustAsync(tenant, product, admin, onHand);
        Assert.Equal(HttpStatusCode.OK, stocked.StatusCode);
        return product;
    }

    private Task<HttpResponseMessage> AdjustAsync(Guid tenant, Guid product, string admin, int delta) =>
        SendAsync(HttpMethod.Post, $"/v1/tenants/{tenant}/products/{product}/stock/adjustments", admin, new { delta, reason = "Stock count" });

    private async Task<(long OnHand, long Reserved, long Available)> StockAsync(Guid tenant, Guid product, string admin)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/v1/tenants/{tenant}/products/{product}/stock", admin);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = json.RootElement;
        return (root.GetProperty("onHand").GetInt64(), root.GetProperty("reserved").GetInt64(), root.GetProperty("available").GetInt64());
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string subject, object? body = null)
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Shared.CreateToken(subject));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    // Counts the order's reservation rows as the application role inside the tenant's scope (FORCE RLS).
    private async Task<long> ReservationRowsAsync(Guid tenant, Guid order)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var set = new NpgsqlCommand($"SELECT set_config('{TenantScopedTransaction.SettingName}', @tenant, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("tenant", tenant.ToString());
            await set.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var count = new NpgsqlCommand("SELECT count(*) FROM inventory.reservations WHERE order_id = @order", connection, transaction);
        count.Parameters.AddWithValue("order", order);
        return (long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
