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
using TenantFlow.Orders.Application.Abstractions;
using TenantFlow.Orders.Domain.Orders;
using TenantFlow.Orders.Infrastructure;

namespace TenantFlow.Database.Tests.Orders;

/// <summary>
/// SPEC-0004 criteria 5 and 9 to 14 for submit and cancel, against real PostgreSQL, through the real host: the M4 gate
/// (no oversell under concurrency, illegal transitions rejected) and recovery by repeating an interrupted command.
/// Tenant A has an administrator and two customers; tenant B an administrator.
/// </summary>
public sealed class OrderLifecycleTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
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

    // ---- criterion 5: the M4 gate, twenty orders for the last unit ----

    [Fact]
    public async Task Twenty_concurrent_submits_for_the_last_unit_reserve_it_exactly_once()
    {
        var w = await CreateWorldAsync();
        var product = await StockedProductAsync(w.TenantA, w.AdminA, 1);
        var orders = new List<Guid>();
        for (var i = 0; i < 20; i++)
        {
            orders.Add(await CreateOrderAsync(w.TenantA, w.CustomerX, product, 1));
        }

        var statuses = await Task.WhenAll(orders.Select(order => StatusOfAsync(Action(w.TenantA, order, "submit"), w.CustomerX)));

        Assert.Equal(1, statuses.Count(static s => s == "pending_payment"));
        Assert.Equal(19, statuses.Count(static s => s == "rejected"));
        Assert.Equal((1L, 1L, 0L), await StockAsync(w.TenantA, product, w.AdminA));
    }

    // ---- criterion 9 ----

    [Fact]
    public async Task Submitting_reserves_the_stock_or_rejects_the_order_without_reserving_anything()
    {
        var w = await CreateWorldAsync();
        var product = await StockedProductAsync(w.TenantA, w.AdminA, 3);
        var fits = await CreateOrderAsync(w.TenantA, w.CustomerX, product, 2);
        var tooMany = await CreateOrderAsync(w.TenantA, w.CustomerX, product, 2);

        using var reserved = await SendAsync(HttpMethod.Post, Action(w.TenantA, fits, "submit"), w.CustomerX);
        using var rejected = await SendAsync(HttpMethod.Post, Action(w.TenantA, tooMany, "submit"), w.CustomerX);

        Assert.Equal("pending_payment", (await JsonAsync(reserved)).GetProperty("status").GetString());
        var rejection = await JsonAsync(rejected);
        Assert.Equal(("rejected", "insufficient_stock"), (rejection.GetProperty("status").GetString(), rejection.GetProperty("rejectionReason").GetString()));
        Assert.Equal((3L, 2L, 1L), await StockAsync(w.TenantA, product, w.AdminA));
    }

    // ---- criterion 10 ----

    [Fact]
    public async Task Cancelling_a_draft_or_a_reserved_order_cancels_it_and_gives_the_stock_back()
    {
        var w = await CreateWorldAsync();
        var product = await StockedProductAsync(w.TenantA, w.AdminA, 5);
        var draft = await CreateOrderAsync(w.TenantA, w.CustomerX, product, 1);
        var reserved = await CreateOrderAsync(w.TenantA, w.CustomerX, product, 4);
        Assert.Equal("pending_payment", await StatusOfAsync(Action(w.TenantA, reserved, "submit"), w.CustomerX));

        var draftCancelled = await StatusOfAsync(Action(w.TenantA, draft, "cancel"), w.CustomerX);
        var reservedCancelled = await StatusOfAsync(Action(w.TenantA, reserved, "cancel"), w.CustomerX);

        Assert.Equal(("cancelled", "cancelled"), (draftCancelled, reservedCancelled));
        Assert.Equal((5L, 0L, 5L), await StockAsync(w.TenantA, product, w.AdminA));
    }

    // ---- criterion 11: illegal transitions change nothing ----

    [Fact]
    public async Task A_transition_the_state_table_does_not_allow_is_a_409_and_changes_nothing()
    {
        var w = await CreateWorldAsync();
        var product = await StockedProductAsync(w.TenantA, w.AdminA, 2);
        var reserved = await CreateOrderAsync(w.TenantA, w.CustomerX, product, 2);
        var rejected = await CreateOrderAsync(w.TenantA, w.CustomerX, product, 1);
        var cancelled = await CreateOrderAsync(w.TenantA, w.CustomerX, product, 1);
        var reserving = await CreateOrderAsync(w.TenantA, w.CustomerX, product, 1);
        Assert.Equal("pending_payment", await StatusOfAsync(Action(w.TenantA, reserved, "submit"), w.CustomerX));
        Assert.Equal("rejected", await StatusOfAsync(Action(w.TenantA, rejected, "submit"), w.CustomerX));
        Assert.Equal("cancelled", await StatusOfAsync(Action(w.TenantA, cancelled, "cancel"), w.CustomerX));
        await SetStatusAsync(w.TenantA, reserving, "pending_reservation");

        var refused = new List<(HttpStatusCode, string?)>();
        foreach (var (order, action) in new[] { (reserved, "submit"), (rejected, "submit"), (cancelled, "submit"), (rejected, "cancel"), (reserving, "cancel") })
        {
            using var response = await SendAsync(HttpMethod.Post, Action(w.TenantA, order, action), w.CustomerX);
            refused.Add((response.StatusCode, await CodeAsync(response)));
        }

        Assert.All(refused, static r => Assert.Equal((HttpStatusCode.Conflict, "orders.invalid_transition"), r));
        Assert.Equal(["pending_payment", "rejected", "cancelled", "pending_reservation"],
            [await StatusAsync(w.TenantA, reserved), await StatusAsync(w.TenantA, rejected), await StatusAsync(w.TenantA, cancelled), await StatusAsync(w.TenantA, reserving)]);
        Assert.Equal((2L, 2L, 0L), await StockAsync(w.TenantA, product, w.AdminA));
    }

    // ---- criterion 11, under concurrency: a cancel racing a submit never loses its answer ----

    [Fact]
    public async Task A_draft_cancelled_and_submitted_at_once_ends_in_one_consistent_state()
    {
        var w = await CreateWorldAsync();
        var product = await StockedProductAsync(w.TenantA, w.AdminA, 1000);
        var violations = new List<string>();
        for (var round = 0; round < 20; round++)
        {
            var order = await CreateOrderAsync(w.TenantA, w.CustomerX, product, 1);
            var submitting = SendAsync(HttpMethod.Post, Action(w.TenantA, order, "submit"), w.CustomerX);
            var cancelling = SendAsync(HttpMethod.Post, Action(w.TenantA, order, "cancel"), w.CustomerX);
            using var submit = await submitting;
            using var cancel = await cancelling;
            var final = await StatusAsync(w.TenantA, order);

            // A cancel that answered 200 must stick, and a cancelled order holds no stock.
            if (cancel.StatusCode == HttpStatusCode.OK && final != "cancelled")
            {
                violations.Add($"round {round}: cancel answered 200 but the order ended {final} (submit {(int)submit.StatusCode})");
            }
        }

        Assert.Empty(violations);
        Assert.Equal(await CountPendingPaymentUnitsAsync(w.TenantA), (await StockAsync(w.TenantA, product, w.AdminA)).Reserved);
    }

    [Fact]
    public async Task A_submit_moves_only_the_draft_version_it_read_so_a_concurrent_line_edit_is_never_reserved_stale()
    {
        var w = await CreateWorldAsync();
        var product = await StockedProductAsync(w.TenantA, w.AdminA, 5);
        using var created = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/orders", w.CustomerX,
            new { lines = new[] { new { productId = product, quantity = 1 } } });
        var order = Id(created);
        var readVersion = uint.Parse(created.Headers.ETag!.Tag.Trim('"'), System.Globalization.CultureInfo.InvariantCulture);
        // The line edit that commits between a submit's read and its transition.
        using var edited = await SendAsync(HttpMethod.Put, $"/v1/tenants/{w.TenantA}/orders/{order}/lines", w.CustomerX,
            new { lines = new[] { new { productId = product, quantity = 2 } } }, created.Headers.ETag.Tag);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var moved = await TransitionDirectlyAsync(w.TenantA, order, readVersion);

        Assert.False(moved);
        Assert.Equal("draft", await StatusAsync(w.TenantA, order));
    }

    // ---- criterion 12: an interrupted submit or cancel is finished by repeating it ----

    [Fact]
    public async Task Repeating_an_interrupted_submit_or_cancel_finishes_it_without_counting_twice()
    {
        var w = await CreateWorldAsync();
        var product = await StockedProductAsync(w.TenantA, w.AdminA, 5);

        // Submit interrupted after Inventory reserved but before the order recorded it.
        var interruptedSubmit = await CreateOrderAsync(w.TenantA, w.CustomerX, product, 2);
        await SetStatusAsync(w.TenantA, interruptedSubmit, "pending_reservation");
        await ReserveDirectlyAsync(w.TenantA, interruptedSubmit, product, 2);
        var resubmitted = await StatusOfAsync(Action(w.TenantA, interruptedSubmit, "submit"), w.CustomerX);
        var afterResubmit = await StockAsync(w.TenantA, product, w.AdminA);

        // Cancel interrupted after the order was cancelled but before Inventory released it.
        await SetStatusAsync(w.TenantA, interruptedSubmit, "cancelled");
        var recancelled = await StatusOfAsync(Action(w.TenantA, interruptedSubmit, "cancel"), w.CustomerX);

        Assert.Equal(("pending_payment", "cancelled"), (resubmitted, recancelled));
        Assert.Equal((5L, 2L, 3L), afterResubmit);
        Assert.Equal((5L, 0L, 5L), await StockAsync(w.TenantA, product, w.AdminA));
    }

    // ---- criterion 13 ----

    [Fact]
    public async Task Only_the_owner_submits_and_an_administrator_may_cancel_any_order()
    {
        var w = await CreateWorldAsync();
        var product = await StockedProductAsync(w.TenantA, w.AdminA, 5);
        var order = await CreateOrderAsync(w.TenantA, w.CustomerX, product, 2);

        using var otherSubmit = await SendAsync(HttpMethod.Post, Action(w.TenantA, order, "submit"), w.CustomerY);
        using var adminSubmit = await SendAsync(HttpMethod.Post, Action(w.TenantA, order, "submit"), w.AdminA);
        var ownerSubmit = await StatusOfAsync(Action(w.TenantA, order, "submit"), w.CustomerX);
        using var otherCancel = await SendAsync(HttpMethod.Post, Action(w.TenantA, order, "cancel"), w.CustomerY);
        var stillReserved = await StockAsync(w.TenantA, product, w.AdminA);
        var adminCancel = await StatusOfAsync(Action(w.TenantA, order, "cancel"), w.AdminA);

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound), (otherSubmit.StatusCode, adminSubmit.StatusCode, otherCancel.StatusCode));
        Assert.Equal(("pending_payment", "cancelled"), (ownerSubmit, adminCancel));
        Assert.Equal((5L, 2L, 3L), stillReserved);
        Assert.Equal((5L, 0L, 5L), await StockAsync(w.TenantA, product, w.AdminA));
    }

    // ---- criterion 14: the cross-tenant denial ----

    [Fact]
    public async Task Another_tenants_order_cannot_be_submitted_or_cancelled()
    {
        var w = await CreateWorldAsync();
        var bProduct = await StockedProductAsync(w.TenantB, w.AdminB, 5);
        var bDraft = await CreateOrderAsync(w.TenantB, w.AdminB, bProduct, 1);
        var bReserved = await CreateOrderAsync(w.TenantB, w.AdminB, bProduct, 2);
        Assert.Equal("pending_payment", await StatusOfAsync(Action(w.TenantB, bReserved, "submit"), w.AdminB));

        // Tenant A's administrator names tenant A in the route but tenant B's orders.
        using var submit = await SendAsync(HttpMethod.Post, Action(w.TenantA, bDraft, "submit"), w.AdminA);
        using var cancel = await SendAsync(HttpMethod.Post, Action(w.TenantA, bReserved, "cancel"), w.AdminA);

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (submit.StatusCode, cancel.StatusCode));
        Assert.Equal(("draft", "pending_payment"), (await StatusAsync(w.TenantB, bDraft), await StatusAsync(w.TenantB, bReserved)));
        Assert.Equal((5L, 2L, 3L), await StockAsync(w.TenantB, bProduct, w.AdminB));
    }

    // ---- helpers ----

    private static string Action(Guid tenant, Guid order, string action) => $"/v1/tenants/{tenant}/orders/{order}/{action}";

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

    private async Task<Guid> StockedProductAsync(Guid tenant, string admin, int onHand)
    {
        using var created = await SendAsync(HttpMethod.Post, $"/v1/tenants/{tenant}/products", admin,
            new { sku = $"SKU-{Guid.NewGuid():N}", name = "Widget", price = new { amount = 1m, currency = "EUR" } });
        var product = Id(created);
        using var stocked = await SendAsync(HttpMethod.Post, $"/v1/tenants/{tenant}/products/{product}/stock/adjustments", admin, new { delta = onHand, reason = "Stock count" });
        Assert.Equal(HttpStatusCode.OK, stocked.StatusCode);
        return product;
    }

    private async Task<Guid> CreateOrderAsync(Guid tenant, string customer, Guid product, int quantity)
    {
        using var created = await SendAsync(HttpMethod.Post, $"/v1/tenants/{tenant}/orders", customer,
            new { lines = new[] { new { productId = product, quantity } } });
        return Id(created);
    }

    private static Guid Id(HttpResponseMessage created)
    {
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return Guid.Parse(created.Headers.Location!.ToString().Split('/')[^1]);
    }

    private async Task<string?> StatusOfAsync(string actionPath, string subject)
    {
        using var response = await SendAsync(HttpMethod.Post, actionPath, subject);
        return (await JsonAsync(response)).GetProperty("status").GetString();
    }

    private async Task<(long OnHand, long Reserved, long Available)> StockAsync(Guid tenant, Guid product, string admin)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/v1/tenants/{tenant}/products/{product}/stock", admin);
        var root = await JsonAsync(response);
        return (root.GetProperty("onHand").GetInt64(), root.GetProperty("reserved").GetInt64(), root.GetProperty("available").GetInt64());
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
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {text}");
        using var json = JsonDocument.Parse(text);
        return json.RootElement.Clone();
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return json.RootElement.GetProperty("code").GetString();
    }

    // Simulates the step an interrupted submit had already done: Inventory's reservation, through its contract.
    private async Task ReserveDirectlyAsync(Guid tenant, Guid order, Guid product, int quantity)
    {
        var services = new ServiceCollection();
        services.AddInventoryModule(fixture.Database.AppConnectionString);
        services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenant));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IStockReservations>()
            .ReserveAsync(order, [new ReservationLine(product, quantity)], TestContext.Current.CancellationToken);
        Assert.Equal(ReservationOutcome.Reserved, outcome);
    }

    // Units held by the tenant's reserved orders: what Inventory's reserved count must equal.
    private async Task<long> CountPendingPaymentUnitsAsync(Guid tenant) =>
        Convert.ToInt64(await InTenantAsync(tenant,
            "SELECT coalesce(sum(l.quantity), 0) FROM orders.order_lines l JOIN orders.orders o ON o.id = l.order_id WHERE o.status = 'pending_payment' AND @order IS NOT NULL",
            Guid.Empty, null), System.Globalization.CultureInfo.InvariantCulture);

    // The submit's first step through the Orders store, as the use case runs it, with the version the submit had read.
    private async Task<bool> TransitionDirectlyAsync(Guid tenant, Guid order, uint readVersion)
    {
        var services = new ServiceCollection();
        services.AddOrdersModule(fixture.Database.AppConnectionString);
        services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenant));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IOrderStore>()
            .TryTransitionAsync(order, OrderStatus.Draft, OrderStatus.PendingReservation, null, readVersion, null, TestContext.Current.CancellationToken);
    }

    private async Task<string?> StatusAsync(Guid tenant, Guid order) =>
        (string?)await InTenantAsync(tenant, "SELECT status FROM orders.orders WHERE id = @order", order, null);

    // Simulates an interruption: the state a crash between two committed steps would leave.
    private Task<object?> SetStatusAsync(Guid tenant, Guid order, string status) =>
        InTenantAsync(tenant, "UPDATE orders.orders SET status = @status WHERE id = @order", order, status);

    // Runs as the application role inside the tenant's scope: the owner role cannot see the rows (FORCE RLS).
    private async Task<object?> InTenantAsync(Guid tenant, string sql, Guid order, string? status)
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
        if (status is not null)
        {
            command.Parameters.AddWithValue("status", status);
        }

        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return result;
    }
}
