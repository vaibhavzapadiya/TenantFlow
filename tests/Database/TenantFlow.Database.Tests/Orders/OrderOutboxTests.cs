using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.Orders.Application.Abstractions;
using TenantFlow.Orders.Domain.Orders;
using TenantFlow.Orders.Infrastructure;

namespace TenantFlow.Database.Tests.Orders;

/// <summary>
/// SPEC-0005 criteria 1 to 3: the Orders outbox is written in the same transaction as the state change, against real
/// PostgreSQL, through the real host where the use case decides the event.
/// </summary>
public sealed class OrderOutboxTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
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

    private sealed record OutboxRow(string Type, int Version, Guid TenantId, JsonElement Payload, string CorrelationId, string? TraceParent);

    // ---- criteria 1 and 3 ----

    [Fact]
    public async Task Reserving_and_cancelling_write_one_event_each_in_the_orders_tenant_and_a_rejection_writes_none()
    {
        var (tenant, admin, customer) = await CreateWorldAsync();
        var product = await StockedProductAsync(tenant, admin, 2);
        var reserved = await CreateOrderAsync(tenant, customer, product, 2);
        var rejected = await CreateOrderAsync(tenant, customer, product, 1);
        var draft = await CreateOrderAsync(tenant, customer, product, 1);

        Assert.Equal("pending_payment", await StatusOfAsync(tenant, reserved, "submit", customer));
        Assert.Equal("rejected", await StatusOfAsync(tenant, rejected, "submit", customer));
        Assert.Equal("cancelled", await StatusOfAsync(tenant, reserved, "cancel", customer));
        Assert.Equal("cancelled", await StatusOfAsync(tenant, draft, "cancel", customer));

        Assert.Empty(await OutboxRowsAsync(rejected));
        var reservedRows = await OutboxRowsAsync(reserved);
        Assert.Equal(["order.reserved.v1", "order.cancelled.v1"], reservedRows.Select(static r => r.Type));
        Assert.All(reservedRows, r => Assert.Equal((1, tenant), (r.Version, r.TenantId)));
        var reservation = reservedRows[0].Payload;
        // Identifiers, status and amounts only: no names, emails or free text (criterion 3).
        Assert.Equal(["lineCount", "orderId", "ownerMembershipId", "total"], reservation.EnumerateObject().Select(static p => p.Name).Order());
        Assert.Equal((reserved, 1, 2m, "EUR"), (
            reservation.GetProperty("orderId").GetGuid(),
            reservation.GetProperty("lineCount").GetInt32(),
            reservation.GetProperty("total").GetProperty("amount").GetDecimal(),
            reservation.GetProperty("total").GetProperty("currency").GetString()));
        Assert.Equal(["orderId", "ownerMembershipId", "previousStatus"], reservedRows[1].Payload.EnumerateObject().Select(static p => p.Name).Order());
        Assert.Equal("pending_payment", reservedRows[1].Payload.GetProperty("previousStatus").GetString());
        Assert.Equal("draft", Assert.Single(await OutboxRowsAsync(draft)).Payload.GetProperty("previousStatus").GetString());

        // The envelope carries the request's W3C trace, and the correlation id is its trace id.
        Assert.All(reservedRows, r =>
        {
            Assert.Matches("^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$", r.TraceParent);
            Assert.Equal(r.TraceParent!.Split('-')[1], r.CorrelationId);
        });
    }

    // ---- criterion 2 ----

    [Fact]
    public async Task A_transition_that_loses_its_conditional_update_writes_no_event()
    {
        var (tenant, admin, customer) = await CreateWorldAsync();
        var product = await StockedProductAsync(tenant, admin, 1);
        var order = await CreateOrderAsync(tenant, customer, product, 1);

        // The order is a draft, so a racing request's expected status no longer matches.
        var moved = await TransitionDirectlyAsync(tenant, order, OrderStatus.PendingReservation, OrderStatus.PendingPayment, Event(order));

        Assert.False(moved);
        Assert.Empty(await OutboxRowsAsync(order));
    }

    [Fact]
    public async Task When_the_outbox_write_fails_the_state_change_rolls_back_with_it()
    {
        var (tenant, admin, customer) = await CreateWorldAsync();
        var product = await StockedProductAsync(tenant, admin, 1);
        var order = await CreateOrderAsync(tenant, customer, product, 1);

        // A payload PostgreSQL refuses as jsonb makes the insert fail after the update has run.
        await Assert.ThrowsAsync<PostgresException>(() =>
            TransitionDirectlyAsync(tenant, order, OrderStatus.Draft, OrderStatus.PendingReservation, new OutgoingMessage("order.test.v1", 1, "not json")));

        Assert.Equal("draft", await StatusOfAsync(tenant, order, null, customer));
        Assert.Empty(await OutboxRowsAsync(order));
    }

    // ---- helpers ----

    private static OutgoingMessage Event(Guid order) => new("order.test.v1", 1, $$"""{"orderId":"{{order}}"}""");

    private async Task<(Guid Tenant, string Admin, string Customer)> CreateWorldAsync()
    {
        var harness = fixture.Harness;
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = await harness.CreateTenantAsync("Outbox tenant");
        var (admin, customer) = ($"admin-{suffix}", $"customer-{suffix}");
        await harness.AddAsync(tenant, admin, "tenant-admin", TestIssuer.Issuer);
        await harness.AddAsync(tenant, customer, "customer", TestIssuer.Issuer);
        return (tenant, admin, customer);
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

    // POSTs the action, or GETs the order when there is none, and returns its status.
    private async Task<string?> StatusOfAsync(Guid tenant, Guid order, string? action, string subject)
    {
        var path = $"/v1/tenants/{tenant}/orders/{order}";
        using var response = action is null
            ? await SendAsync(HttpMethod.Get, path, subject)
            : await SendAsync(HttpMethod.Post, $"{path}/{action}", subject);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {text}");
        using var json = JsonDocument.Parse(text);
        return json.RootElement.GetProperty("status").GetString();
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

    // The transition through the Orders store, as the use case calls it.
    private async Task<bool> TransitionDirectlyAsync(Guid tenant, Guid order, OrderStatus expected, OrderStatus target, OutgoingMessage message)
    {
        var services = new ServiceCollection();
        services.AddOrdersModule(fixture.Database.AppConnectionString);
        services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenant));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IOrderStore>()
            .TryTransitionAsync(order, expected, target, null, null, message, TestContext.Current.CancellationToken);
    }

    // As the application role with no tenant set: the outbox is not under row-level security (ADR-0021).
    private async Task<List<OutboxRow>> OutboxRowsAsync(Guid order)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT type, version, tenant_id, payload::text, correlation_id, traceparent FROM orders.outbox_messages WHERE payload->>'orderId' = @order ORDER BY occurred_at, id",
            connection);
        command.Parameters.AddWithValue("order", order.ToString());
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<OutboxRow>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            using var payload = JsonDocument.Parse(reader.GetString(3));
            rows.Add(new OutboxRow(reader.GetString(0), reader.GetInt32(1), reader.GetGuid(2), payload.RootElement.Clone(), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return rows;
    }
}
