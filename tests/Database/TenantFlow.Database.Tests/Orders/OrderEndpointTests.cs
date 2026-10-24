using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using TenantFlow.BuildingBlocks.Persistence;

namespace TenantFlow.Database.Tests.Orders;

/// <summary>
/// SPEC-0004 criteria 6 to 8, 11 (edits), 13 and 14 against real PostgreSQL, through the real host with real tenant-scheme
/// tokens. Two tenants: A with an administrator and two customers, B with an administrator.
/// </summary>
public sealed class OrderEndpointTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
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

    private sealed record World(Guid TenantA, Guid TenantB, string AdminA, string CustomerX, string CustomerY, string AdminB);

    // ---- criterion 6 ----

    [Fact]
    public async Task An_order_snapshots_its_products_and_a_later_price_change_does_not_touch_it()
    {
        var w = await CreateWorldAsync();
        var product = await CreateProductAsync(w.TenantA, w.AdminA, 2.5m);

        using var created = await SendAsync(HttpMethod.Post, OrdersPath(w.TenantA), w.CustomerX, Lines((product, 3)));
        using var _ = await SendAsync(HttpMethod.Put, $"/v1/tenants/{w.TenantA}/products/{product}", w.AdminA,
            new { name = "Renamed", price = new { amount = 9m, currency = "EUR" }, status = "active" }, ifMatch: "*");
        using var read = await SendAsync(HttpMethod.Get, created.Headers.Location!.ToString(), w.CustomerX);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.NotNull(created.Headers.ETag);
        var order = await JsonAsync(read);
        Assert.Equal("draft", order.GetProperty("status").GetString());
        Assert.Equal(7.5m, order.GetProperty("total").GetProperty("amount").GetDecimal());
        var line = Assert.Single(order.GetProperty("lines").EnumerateArray());
        Assert.Equal(("Widget", 2.5m, 3, 7.5m), (line.GetProperty("name").GetString(), line.GetProperty("unitPrice").GetProperty("amount").GetDecimal(),
            line.GetProperty("quantity").GetInt32(), line.GetProperty("lineTotal").GetProperty("amount").GetDecimal()));
    }

    // ---- criterion 7 ----

    [Fact]
    public async Task Invalid_lines_are_refused_and_nothing_is_written()
    {
        var w = await CreateWorldAsync();
        var euro = await CreateProductAsync(w.TenantA, w.AdminA, 1m);
        var dollar = await CreateProductAsync(w.TenantA, w.AdminA, 1m, "USD");
        var archived = await CreateProductAsync(w.TenantA, w.AdminA, 1m);
        using var archive = await SendAsync(HttpMethod.Put, $"/v1/tenants/{w.TenantA}/products/{archived}", w.AdminA,
            new { name = "Widget", price = new { amount = 1m, currency = "EUR" }, status = "archived" }, ifMatch: "*");

        using var empty = await SendAsync(HttpMethod.Post, OrdersPath(w.TenantA), w.CustomerX, new { lines = Array.Empty<object>() });
        using var unknown = await SendAsync(HttpMethod.Post, OrdersPath(w.TenantA), w.CustomerX, Lines((Guid.CreateVersion7(), 1)));
        using var unavailable = await SendAsync(HttpMethod.Post, OrdersPath(w.TenantA), w.CustomerX, Lines((archived, 1)));
        using var mixed = await SendAsync(HttpMethod.Post, OrdersPath(w.TenantA), w.CustomerX, Lines((euro, 1), (dollar, 1)));

        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.Conflict, HttpStatusCode.Conflict),
            (empty.StatusCode, unknown.StatusCode, unavailable.StatusCode, mixed.StatusCode));
        Assert.Equal(("catalog.product_not_found", "orders.product_unavailable", "orders.mixed_currency"),
            (await CodeAsync(unknown), await CodeAsync(unavailable), await CodeAsync(mixed)));
        Assert.Equal(0L, await OrderRowsAsync(w.TenantA));
    }

    // ---- criterion 8 ----

    [Fact]
    public async Task Lines_change_only_with_the_current_etag()
    {
        var w = await CreateWorldAsync();
        var first = await CreateProductAsync(w.TenantA, w.AdminA, 1m);
        var second = await CreateProductAsync(w.TenantA, w.AdminA, 4m);
        using var created = await SendAsync(HttpMethod.Post, OrdersPath(w.TenantA), w.CustomerX, Lines((first, 1)));
        var linesPath = $"{created.Headers.Location}/lines";
        var etag = created.Headers.ETag!.Tag;

        using var missing = await SendAsync(HttpMethod.Put, linesPath, w.CustomerX, Lines((second, 2)));
        using var changed = await SendAsync(HttpMethod.Put, linesPath, w.CustomerX, Lines((second, 2)), ifMatch: etag);
        using var stale = await SendAsync(HttpMethod.Put, linesPath, w.CustomerX, Lines((first, 5)), ifMatch: etag);
        using var read = await SendAsync(HttpMethod.Get, created.Headers.Location!.ToString(), w.CustomerX);

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.OK, HttpStatusCode.PreconditionFailed), (missing.StatusCode, changed.StatusCode, stale.StatusCode));
        Assert.Equal(("orders.if_match_required", "orders.version_conflict"), (await CodeAsync(missing), await CodeAsync(stale)));
        Assert.NotEqual(etag, changed.Headers.ETag!.Tag);
        var order = await JsonAsync(read);
        Assert.Equal(8m, order.GetProperty("total").GetProperty("amount").GetDecimal());
        Assert.Equal(second, Assert.Single(order.GetProperty("lines").EnumerateArray()).GetProperty("productId").GetGuid());
    }

    // ---- criterion 11, for edits (submit and cancel arrive in slice 3) ----

    [Fact]
    public async Task An_order_past_the_draft_stage_cannot_be_edited()
    {
        var w = await CreateWorldAsync();
        var product = await CreateProductAsync(w.TenantA, w.AdminA, 1m);
        using var created = await SendAsync(HttpMethod.Post, OrdersPath(w.TenantA), w.CustomerX, Lines((product, 1)));
        var order = Id(created);
        await ExecuteInTenantAsync(w.TenantA, "UPDATE orders.orders SET status = 'pending_payment' WHERE id = @order", order);

        using var edit = await SendAsync(HttpMethod.Put, $"{created.Headers.Location}/lines", w.CustomerX, Lines((product, 7)), ifMatch: "*");

        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Equal("orders.invalid_transition", await CodeAsync(edit));
        Assert.Equal(1L, await ScalarAsync(w.TenantA, "SELECT sum(quantity) FROM orders.order_lines WHERE order_id = @order", order));
    }

    // ---- criterion 13 ----

    [Fact]
    public async Task Another_customers_order_does_not_exist_for_them_and_an_admin_reads_but_does_not_edit_it()
    {
        var w = await CreateWorldAsync();
        var product = await CreateProductAsync(w.TenantA, w.AdminA, 1m);
        using var created = await SendAsync(HttpMethod.Post, OrdersPath(w.TenantA), w.CustomerX, Lines((product, 1)));
        var path = created.Headers.Location!.ToString();

        using var otherRead = await SendAsync(HttpMethod.Get, path, w.CustomerY);
        using var otherEdit = await SendAsync(HttpMethod.Put, $"{path}/lines", w.CustomerY, Lines((product, 9)), ifMatch: "*");
        using var adminRead = await SendAsync(HttpMethod.Get, path, w.AdminA);
        using var adminEdit = await SendAsync(HttpMethod.Put, $"{path}/lines", w.AdminA, Lines((product, 9)), ifMatch: "*");

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.OK, HttpStatusCode.NotFound),
            (otherRead.StatusCode, otherEdit.StatusCode, adminRead.StatusCode, adminEdit.StatusCode));
        Assert.Equal("orders.order_not_found", await CodeAsync(otherEdit));
        Assert.Equal(1L, await ScalarAsync(w.TenantA, "SELECT sum(quantity) FROM orders.order_lines WHERE order_id = @order", Id(created)));
    }

    // ---- criterion 14: the cross-tenant denial ----

    [Fact]
    public async Task Another_tenants_order_cannot_be_read_or_changed()
    {
        var w = await CreateWorldAsync();
        var bProduct = await CreateProductAsync(w.TenantB, w.AdminB, 1m);
        var aProduct = await CreateProductAsync(w.TenantA, w.AdminA, 1m);
        using var created = await SendAsync(HttpMethod.Post, OrdersPath(w.TenantB), w.AdminB, Lines((bProduct, 2)));
        var bOrder = Id(created);

        // Tenant A's administrator names tenant A in the route but tenant B's order.
        using var read = await SendAsync(HttpMethod.Get, $"{OrdersPath(w.TenantA)}/{bOrder}", w.AdminA);
        using var edit = await SendAsync(HttpMethod.Put, $"{OrdersPath(w.TenantA)}/{bOrder}/lines", w.AdminA, Lines((aProduct, 9)), ifMatch: "*");

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (read.StatusCode, edit.StatusCode));
        Assert.Equal(2L, await ScalarAsync(w.TenantB, "SELECT sum(quantity) FROM orders.order_lines WHERE order_id = @order", bOrder));
        Assert.Equal(0L, await OrderRowsAsync(w.TenantA));
    }

    // ---- helpers ----

    private static string OrdersPath(Guid tenant) => $"/v1/tenants/{tenant}/orders";

    private static object Lines(params (Guid Product, int Quantity)[] lines) =>
        new { lines = lines.Select(static l => new { productId = l.Product, quantity = l.Quantity }).ToArray() };

    private async Task<World> CreateWorldAsync()
    {
        var harness = fixture.Harness;
        var suffix = Guid.NewGuid().ToString("N");
        var a = await harness.CreateTenantAsync("Tenant A");
        var b = await harness.CreateTenantAsync("Tenant B");
        var world = new World(a, b, $"admin-a-{suffix}", $"customer-x-{suffix}", $"customer-y-{suffix}", $"admin-b-{suffix}");
        await harness.AddAsync(a, world.AdminA, "tenant-admin", TestIssuer.Issuer);
        await harness.AddAsync(a, world.CustomerX, "customer", TestIssuer.Issuer);
        await harness.AddAsync(a, world.CustomerY, "customer", TestIssuer.Issuer);
        await harness.AddAsync(b, world.AdminB, "tenant-admin", TestIssuer.Issuer);
        return world;
    }

    private async Task<Guid> CreateProductAsync(Guid tenant, string admin, decimal price, string currency = "EUR")
    {
        using var created = await SendAsync(HttpMethod.Post, $"/v1/tenants/{tenant}/products", admin,
            new { sku = $"SKU-{Guid.NewGuid():N}", name = "Widget", price = new { amount = price, currency } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return Id(created);
    }

    private static Guid Id(HttpResponseMessage created)
    {
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return Guid.Parse(created.Headers.Location!.ToString().Split('/')[^1]);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string subject, object? body = null, string? ifMatch = null)
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Shared.CreateToken(subject));
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return json.RootElement.Clone();
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return json.RootElement.GetProperty("code").GetString();
    }

    private Task<long> OrderRowsAsync(Guid tenant) =>
        ScalarAsync(tenant, "SELECT count(*) FROM orders.orders WHERE @order IS NOT NULL", Guid.Empty);

    // Reads as the application role inside the tenant's scope: the owner role cannot see the rows (FORCE RLS).
    private async Task<long> ScalarAsync(Guid tenant, string sql, Guid order) =>
        Convert.ToInt64(await InTenantAsync(tenant, sql, order, static command => command.ExecuteScalarAsync(TestContext.Current.CancellationToken)), System.Globalization.CultureInfo.InvariantCulture);

    private Task<object?> ExecuteInTenantAsync(Guid tenant, string sql, Guid order) =>
        InTenantAsync(tenant, sql, order, static async command => (object?)await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

    private async Task<object?> InTenantAsync(Guid tenant, string sql, Guid order, Func<NpgsqlCommand, Task<object?>> run)
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
        command.Parameters.AddWithValue("order", order);
        var result = await run(command);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return result;
    }
}
