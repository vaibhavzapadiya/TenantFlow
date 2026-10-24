using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using TenantFlow.BuildingBlocks.Persistence;

namespace TenantFlow.Database.Tests.Inventory;

/// <summary>
/// SPEC-0003 criteria 11 to 16 against real PostgreSQL, through the real host with real tenant-scheme tokens. Two
/// tenants, A and B, each with an administrator, and a customer in A.
/// </summary>
public sealed class StockEndpointTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
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

    private sealed record World(Guid TenantA, Guid TenantB, string AdminA, string CustomerA, string AdminB);

    // ---- criterion 11 ----

    [Fact]
    public async Task Stock_starts_at_zero_and_moves_by_exactly_each_signed_delta()
    {
        var w = await CreateWorldAsync();
        var product = await CreateProductAsync(w.TenantA, w.AdminA);

        var before = await OnHandAsync(await SendAsync(HttpMethod.Get, StockPath(w.TenantA, product), w.AdminA));
        var afterIn = await OnHandAsync(await AdjustAsync(w.TenantA, product, w.AdminA, 5));
        var afterOut = await OnHandAsync(await AdjustAsync(w.TenantA, product, w.AdminA, -2));
        var read = await OnHandAsync(await SendAsync(HttpMethod.Get, StockPath(w.TenantA, product), w.AdminA));

        Assert.Equal((0L, 5L, 3L, 3L), (before, afterIn, afterOut, read));
        Assert.Equal(2L, await RowsAsync(w.TenantA, "SELECT count(*) FROM inventory.stock_adjustments WHERE product_id = @product", product));
    }

    // ---- criterion 12 ----

    [Fact]
    public async Task An_adjustment_that_would_go_negative_is_a_409_and_changes_nothing()
    {
        var w = await CreateWorldAsync();
        var product = await CreateProductAsync(w.TenantA, w.AdminA);
        using var _ = await AdjustAsync(w.TenantA, product, w.AdminA, 3);

        using var refused = await AdjustAsync(w.TenantA, product, w.AdminA, -4);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("inventory.insufficient_stock", await CodeAsync(refused));
        Assert.Equal(3L, await OnHandAsync(await SendAsync(HttpMethod.Get, StockPath(w.TenantA, product), w.AdminA)));
        Assert.Equal(1L, await RowsAsync(w.TenantA, "SELECT count(*) FROM inventory.stock_adjustments WHERE product_id = @product", product));
    }

    // ---- criterion 13: the concurrency invariant, on the real database ----

    [Fact]
    public async Task Twenty_concurrent_decrements_of_ten_units_succeed_exactly_ten_times()
    {
        var w = await CreateWorldAsync();
        var product = await CreateProductAsync(w.TenantA, w.AdminA);
        using var _ = await AdjustAsync(w.TenantA, product, w.AdminA, 10);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => AdjustAsync(w.TenantA, product, w.AdminA, -1)));
        var statuses = responses.Select(static r => r.StatusCode).ToList();
        foreach (var response in responses)
        {
            response.Dispose();
        }

        Assert.Equal(10, statuses.Count(static s => s == HttpStatusCode.OK));
        Assert.Equal(10, statuses.Count(static s => s == HttpStatusCode.Conflict));
        Assert.Equal(0L, await OnHandAsync(await SendAsync(HttpMethod.Get, StockPath(w.TenantA, product), w.AdminA)));
        Assert.Equal(11L, await RowsAsync(w.TenantA, "SELECT count(*) FROM inventory.stock_adjustments WHERE product_id = @product", product));
    }

    // ---- the balance is 64-bit: a large balance grows instead of overflowing into a 500 ----

    [Fact]
    public async Task A_balance_beyond_the_32_bit_range_keeps_growing()
    {
        var w = await CreateWorldAsync();
        var product = await CreateProductAsync(w.TenantA, w.AdminA);
        using var _ = await AdjustAsync(w.TenantA, product, w.AdminA, 1);
        await ExecuteInTenantAsync(w.TenantA, "UPDATE inventory.stock_balances SET on_hand = 2147000000 WHERE product_id = @product", product);

        var onHand = await OnHandAsync(await AdjustAsync(w.TenantA, product, w.AdminA, 1_000_000));

        Assert.Equal(2_148_000_000L, onHand);
    }

    // ---- criterion 14 ----

    [Fact]
    public async Task Stock_of_an_unknown_product_is_404_and_creates_no_balance()
    {
        var w = await CreateWorldAsync();
        var unknown = Guid.CreateVersion7();

        using var read = await SendAsync(HttpMethod.Get, StockPath(w.TenantA, unknown), w.AdminA);
        using var adjust = await AdjustAsync(w.TenantA, unknown, w.AdminA, 5);

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal("catalog.product_not_found", await CodeAsync(adjust));
        Assert.Equal(0L, await RowsAsync(w.TenantA, "SELECT count(*) FROM inventory.stock_balances WHERE product_id = @product", unknown));
    }

    // ---- criterion 15 ----

    [Fact]
    public async Task A_customer_can_neither_read_nor_adjust_stock()
    {
        var w = await CreateWorldAsync();
        var product = await CreateProductAsync(w.TenantA, w.AdminA);

        using var read = await SendAsync(HttpMethod.Get, StockPath(w.TenantA, product), w.CustomerA);
        using var adjust = await AdjustAsync(w.TenantA, product, w.CustomerA, 5);

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, adjust.StatusCode);
        Assert.Equal(0L, await RowsAsync(w.TenantA, "SELECT count(*) FROM inventory.stock_balances WHERE product_id = @product", product));
    }

    // ---- criterion 16: the cross-tenant denial ----

    [Fact]
    public async Task Another_tenants_stock_cannot_be_read_or_changed()
    {
        var w = await CreateWorldAsync();
        var bProduct = await CreateProductAsync(w.TenantB, w.AdminB);
        using var _ = await AdjustAsync(w.TenantB, bProduct, w.AdminB, 7);

        // Tenant A's administrator names tenant A in the route but tenant B's product.
        using var read = await SendAsync(HttpMethod.Get, StockPath(w.TenantA, bProduct), w.AdminA);
        using var adjust = await AdjustAsync(w.TenantA, bProduct, w.AdminA, -7);

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, adjust.StatusCode);
        Assert.Equal(7L, await OnHandAsync(await SendAsync(HttpMethod.Get, StockPath(w.TenantB, bProduct), w.AdminB)));
        Assert.Equal(0L, await RowsAsync(w.TenantA, "SELECT count(*) FROM inventory.stock_balances WHERE product_id = @product", bProduct));
    }

    // ---- helpers ----

    private async Task<World> CreateWorldAsync()
    {
        var harness = fixture.Harness;
        var suffix = Guid.NewGuid().ToString("N");
        var a = await harness.CreateTenantAsync("Tenant A");
        var b = await harness.CreateTenantAsync("Tenant B");
        var world = new World(a, b, $"admin-a-{suffix}", $"customer-a-{suffix}", $"admin-b-{suffix}");
        await harness.AddAsync(a, world.AdminA, "tenant-admin", TestIssuer.Issuer);
        await harness.AddAsync(a, world.CustomerA, "customer", TestIssuer.Issuer);
        await harness.AddAsync(b, world.AdminB, "tenant-admin", TestIssuer.Issuer);
        return world;
    }

    private async Task<Guid> CreateProductAsync(Guid tenant, string admin)
    {
        using var created = await SendAsync(HttpMethod.Post, $"/v1/tenants/{tenant}/products", admin,
            new { sku = $"SKU-{Guid.NewGuid():N}", name = "Widget", price = new { amount = 1m, currency = "EUR" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return Guid.Parse(created.Headers.Location!.ToString().Split('/')[^1]);
    }

    private static string StockPath(Guid tenant, Guid product) => $"/v1/tenants/{tenant}/products/{product}/stock";

    private Task<HttpResponseMessage> AdjustAsync(Guid tenant, Guid product, string subject, int delta) =>
        SendAsync(HttpMethod.Post, $"{StockPath(tenant, product)}/adjustments", subject, new { delta, reason = "Stock count" });

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

    private static async Task<long> OnHandAsync(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            return json.RootElement.GetProperty("onHand").GetInt64();
        }
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return json.RootElement.GetProperty("code").GetString();
    }

    // Counts rows as the application role inside the tenant's scope: the owner role cannot see them (FORCE RLS).
    private async Task<long> RowsAsync(Guid tenant, string sql, Guid product) =>
        (long)(await InTenantAsync(tenant, sql, product, static command => command.ExecuteScalarAsync(TestContext.Current.CancellationToken)))!;

    private Task<object?> ExecuteInTenantAsync(Guid tenant, string sql, Guid product) =>
        InTenantAsync(tenant, sql, product, static async command => (object?)await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

    private async Task<object?> InTenantAsync(Guid tenant, string sql, Guid product, Func<NpgsqlCommand, Task<object?>> run)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var set = new NpgsqlCommand($"SELECT set_config('{TenantScopedTransaction.SettingName}', @tenant, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("tenant", tenant.ToString());
            await set.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("product", product);
        var result = await run(command);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return result;
    }
}
