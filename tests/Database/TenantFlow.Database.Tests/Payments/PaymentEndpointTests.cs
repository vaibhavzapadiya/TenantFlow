using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TenantFlow.BuildingBlocks.Persistence;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.Orders.Application.Abstractions;
using TenantFlow.Orders.Domain.Orders;
using TenantFlow.Orders.Infrastructure;
using TenantFlow.Payments.Application.Abstractions;

namespace TenantFlow.Database.Tests.Payments;

/// <summary>
/// SPEC-0006 criteria 1 to 6 and 15 against real PostgreSQL, through the real host with the deterministic simulator
/// (ADR-0022). The provider is wrapped to count calls per payment, so "never charged twice" is observed, not assumed.
/// Tenant A has an administrator and two customers; tenant B an administrator.
/// </summary>
public sealed class PaymentEndpointTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
{
    private readonly CallLog _calls = new();
    private WebApplicationFactory<Program>? _factory;

    private WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("Not initialized.");

    public ValueTask InitializeAsync()
    {
        _factory = CreateFactory(inner => new CountingProvider(inner, _calls));
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

    // ---- criterion 1 ----

    [Fact]
    public async Task A_reserved_order_is_charged_once_for_its_own_amount_and_announces_the_outcome()
    {
        var w = await CreateWorldAsync();
        var paid = await ReservedOrderAsync(w.TenantA, w.AdminA, w.CustomerX, price: 2.5m, quantity: 3);
        var declined = await ReservedOrderAsync(w.TenantA, w.AdminA, w.CustomerX, price: 1m, quantity: 1);
        using var attempts = new MeterCapture("tenantflow.payments.attempts");

        using var success = await PayAsync(w.TenantA, paid, w.CustomerX, "sim_success", Key());
        using var decline = await PayAsync(w.TenantA, declined, w.CustomerX, "sim_decline", Key());
        using var read = await SendAsync(HttpMethod.Get, success.Headers.Location!.ToString(), w.CustomerX);

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created), (success.StatusCode, decline.StatusCode));
        var payment = await JsonAsync(success);
        Assert.Equal(("succeeded", 7.5m, "EUR", paid), (payment.GetProperty("status").GetString(), payment.GetProperty("amount").GetProperty("amount").GetDecimal(),
            payment.GetProperty("amount").GetProperty("currency").GetString(), payment.GetProperty("orderId").GetGuid()));
        Assert.Equal("failed", (await JsonAsync(decline)).GetProperty("status").GetString());
        Assert.Equal(payment.GetProperty("id").GetGuid(), (await JsonAsync(read)).GetProperty("id").GetGuid());
        var paymentId = payment.GetProperty("id").GetGuid();
        Assert.Equal(1, _calls.For(paymentId));
        Assert.Equal($"sim_pi_{paymentId:N}", await TextAsync(w.TenantA, "SELECT provider_payment_id FROM payments.payments WHERE order_id = @id", paid));

        // One event per outcome, in the order's tenant, carrying identifiers and amounts only.
        var events = await OutboxAsync(w.TenantA);
        Assert.Equal(["payment.failed.v1", "payment.succeeded.v1"], events.Select(static e => e.Type).Order());
        Assert.All(events, e => Assert.Equal(w.TenantA, e.Tenant));
        using var succeeded = JsonDocument.Parse(events.Single(static e => e.Type == "payment.succeeded.v1").Payload);
        Assert.Equal(["amount", "orderId", "paymentId"], succeeded.RootElement.EnumerateObject().Select(static p => p.Name).Order());
        Assert.Equal(paid, succeeded.RootElement.GetProperty("orderId").GetGuid());
        using var failed = JsonDocument.Parse(events.Single(static e => e.Type == "payment.failed.v1").Payload);
        Assert.Equal("card_declined", failed.RootElement.GetProperty("failureCode").GetString());

        // SPEC-0010 criterion 4: each committed outcome is counted once, under its status name only.
        Assert.Equal(["failed", "succeeded"], attempts.Measurements.Select(static m => m.Tags["outcome"]).Order(StringComparer.Ordinal));
        Assert.All(attempts.Measurements, static m => Assert.Equal(["outcome"], m.Tags.Keys));
    }

    // ---- criterion 2 ----

    [Fact]
    public async Task The_same_key_and_body_replay_the_first_response_without_charging_again()
    {
        var w = await CreateWorldAsync();
        var order = await ReservedOrderAsync(w.TenantA, w.AdminA, w.CustomerX);
        var bOrder = await ReservedOrderAsync(w.TenantB, w.AdminB, w.AdminB);
        var key = Key();

        using var first = await PayAsync(w.TenantA, order, w.CustomerX, "sim_success", key);
        using var again = await PayAsync(w.TenantA, order, w.CustomerX, "sim_success", key);
        using var changed = await PayAsync(w.TenantA, order, w.CustomerX, "sim_decline", key);
        using var otherCustomer = await PayAsync(w.TenantA, order, w.CustomerY, "sim_success", key);
        using var missing = await PayAsync(w.TenantA, order, w.CustomerX, "sim_success", key: null);
        using var otherTenant = await PayAsync(w.TenantB, bOrder, w.AdminB, "sim_success", key);

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created), (first.StatusCode, again.StatusCode));
        Assert.Equal(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), await again.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, _calls.For((await JsonAsync(first)).GetProperty("id").GetGuid()));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, HttpStatusCode.UnprocessableEntity, HttpStatusCode.BadRequest),
            (changed.StatusCode, otherCustomer.StatusCode, missing.StatusCode));
        Assert.Equal(("payments.idempotency_key_reused", "payments.idempotency_key_reused"), (await CodeAsync(changed), await CodeAsync(otherCustomer)));
        Assert.Equal("payments.idempotency_key.invalid", await FieldCodeAsync(missing));
        // Keys are scoped to the tenant: tenant B's first use of the same key is a new request.
        Assert.Equal(HttpStatusCode.Created, otherTenant.StatusCode);
        Assert.Equal(1L, await ScalarAsync(w.TenantA, "SELECT count(*) FROM payments.payments WHERE order_id = @id", order));
    }

    [Fact]
    public async Task A_repeat_while_the_first_request_is_still_charging_is_409_and_starts_nothing()
    {
        var w = await CreateWorldAsync();
        var order = await ReservedOrderAsync(w.TenantA, w.AdminA, w.CustomerX);
        var gate = new Gate();
        await using var blocking = CreateFactory(inner => new GatedProvider(inner, gate));
        var key = Key();

        // The first request is inside the provider call, its key claimed but unfinished, when the repeat arrives.
        var firstTask = PayAsync(blocking, w.TenantA, order, w.CustomerX, "sim_success", key);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        using var repeat = await PayAsync(blocking, w.TenantA, order, w.CustomerX, "sim_success", key);
        gate.Release.SetResult();
        using var first = await firstTask;

        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.Created), (repeat.StatusCode, first.StatusCode));
        Assert.Equal("payments.request_in_progress", await CodeAsync(repeat));
        Assert.Equal(1, gate.Calls);
        Assert.Equal(1L, await ScalarAsync(w.TenantA, "SELECT count(*) FROM payments.payments WHERE order_id = @id", order));
    }

    // ---- criterion 3 ----

    [Fact]
    public async Task An_order_with_a_payment_is_not_charged_again_under_a_new_key_and_a_draft_cannot_be_paid()
    {
        var w = await CreateWorldAsync();
        var order = await ReservedOrderAsync(w.TenantA, w.AdminA, w.CustomerX);
        var product = await StockedProductAsync(w.TenantA, w.AdminA, 1m, 1);
        var draft = await CreateOrderAsync(w.TenantA, w.CustomerX, product, 1);

        using var first = await PayAsync(w.TenantA, order, w.CustomerX, "sim_decline", Key());
        using var second = await PayAsync(w.TenantA, order, w.CustomerX, "sim_success", Key());
        using var notReserved = await PayAsync(w.TenantA, draft, w.CustomerX, "sim_success", Key());

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Conflict, HttpStatusCode.Conflict), (first.StatusCode, second.StatusCode, notReserved.StatusCode));
        Assert.Equal(("payments.already_exists", "orders.invalid_transition"), (await CodeAsync(second), await CodeAsync(notReserved)));
        Assert.Equal(1L, await ScalarAsync(w.TenantA, "SELECT count(*) FROM payments.payments WHERE order_id = @id", order));
        Assert.Equal(0L, await ScalarAsync(w.TenantA, "SELECT count(*) FROM payments.payments WHERE order_id = @id", draft));
        Assert.Equal("failed", await TextAsync(w.TenantA, "SELECT status FROM payments.payments WHERE order_id = @id", order));
    }

    // ---- criterion 4: the cross-tenant and cross-customer denial ----

    [Fact]
    public async Task Another_tenants_or_another_customers_order_cannot_be_paid_or_read_and_nothing_is_stored()
    {
        var w = await CreateWorldAsync();
        var bOrder = await ReservedOrderAsync(w.TenantB, w.AdminB, w.AdminB);
        var xOrder = await ReservedOrderAsync(w.TenantA, w.AdminA, w.CustomerX);
        using var paid = await PayAsync(w.TenantA, xOrder, w.CustomerX, "sim_success", Key());

        // Tenant A's administrator names tenant A in the route but tenant B's order; customer Y names customer X's order.
        using var crossTenant = await PayAsync(w.TenantA, bOrder, w.AdminA, "sim_success", Key());
        using var crossCustomer = await PayAsync(w.TenantA, xOrder, w.CustomerY, "sim_success", Key());
        using var readByOther = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}/orders/{xOrder}/payments/current", w.CustomerY);
        using var readAcross = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}/orders/{bOrder}/payments/current", w.AdminA);
        using var readByAdmin = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}/orders/{xOrder}/payments/current", w.AdminA);

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (crossTenant.StatusCode, crossCustomer.StatusCode));
        Assert.Equal(("orders.order_not_found", "orders.order_not_found"), (await CodeAsync(crossTenant), await CodeAsync(crossCustomer)));
        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.OK), (readByOther.StatusCode, readAcross.StatusCode, readByAdmin.StatusCode));
        Assert.Equal(0L, await ScalarAsync(w.TenantB, "SELECT count(*) FROM payments.payments WHERE order_id = @id", bOrder));
        Assert.Equal(0L, await ScalarAsync(w.TenantA, "SELECT count(*) FROM payments.payments WHERE order_id = @id", bOrder));
        // Nobody else's attempt marked B's order as begun: its owner can still cancel it.
        Assert.Null(await TextAsync(w.TenantB, "SELECT payment_started_at::text FROM orders.orders WHERE id = @id", bOrder));
    }

    // ---- criterion 5 ----

    [Fact]
    public async Task A_timeout_or_server_error_records_an_unknown_outcome_shown_as_processing_with_no_event()
    {
        var w = await CreateWorldAsync();
        var timedOut = await ReservedOrderAsync(w.TenantA, w.AdminA, w.CustomerX);
        var serverError = await ReservedOrderAsync(w.TenantA, w.AdminA, w.CustomerX);

        using var timeout = await PayAsync(w.TenantA, timedOut, w.CustomerX, "sim_timeout_then_success", Key());
        using var error = await PayAsync(w.TenantA, serverError, w.CustomerX, "sim_server_error", Key());

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created), (timeout.StatusCode, error.StatusCode));
        Assert.Equal(("processing", "processing"), ((await JsonAsync(timeout)).GetProperty("status").GetString(), (await JsonAsync(error)).GetProperty("status").GetString()));
        Assert.Equal("unknown", await TextAsync(w.TenantA, "SELECT status FROM payments.payments WHERE order_id = @id", timedOut));
        Assert.Equal("unknown", await TextAsync(w.TenantA, "SELECT status FROM payments.payments WHERE order_id = @id", serverError));
        Assert.Equal(1, _calls.For((await JsonAsync(timeout)).GetProperty("id").GetGuid()));
        Assert.Empty(await OutboxAsync(w.TenantA));
    }

    // ---- criterion 6 ----

    [Fact]
    public async Task Once_a_payment_has_begun_neither_the_owner_nor_an_admin_can_cancel_the_order()
    {
        var w = await CreateWorldAsync();
        var order = await ReservedOrderAsync(w.TenantA, w.AdminA, w.CustomerX);
        using var paying = await PayAsync(w.TenantA, order, w.CustomerX, "sim_timeout_unknown", Key());

        using var byOwner = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/orders/{order}/cancel", w.CustomerX);
        using var byAdmin = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/orders/{order}/cancel", w.AdminA);

        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.Conflict), (byOwner.StatusCode, byAdmin.StatusCode));
        Assert.Equal(("orders.payment_in_progress", "orders.payment_in_progress"), (await CodeAsync(byOwner), await CodeAsync(byAdmin)));
        Assert.Equal("pending_payment", await TextAsync(w.TenantA, "SELECT status FROM orders.orders WHERE id = @id", order));
    }

    [Fact]
    public async Task A_cancel_that_read_the_order_before_the_payment_began_loses_at_the_store()
    {
        var w = await CreateWorldAsync();
        var order = await ReservedOrderAsync(w.TenantA, w.AdminA, w.CustomerX);
        // The payment begins between the cancel's read and its write: the conditional update must miss.
        await ExecuteAsync(w.TenantA, "UPDATE orders.orders SET payment_started_at = now() WHERE id = @id", order);

        var cancelled = await TransitionDirectlyAsync(w.TenantA, order, OrderStatus.PendingPayment, OrderStatus.Cancelled);

        Assert.False(cancelled);
        Assert.Equal("pending_payment", await TextAsync(w.TenantA, "SELECT status FROM orders.orders WHERE id = @id", order));
    }

    // M6 review fix 2: nothing to charge is refused before the order is marked, so it stays cancellable.
    [Fact]
    public async Task An_order_with_nothing_to_pay_is_refused_and_stays_cancellable()
    {
        var w = await CreateWorldAsync();
        var free = await ReservedOrderAsync(w.TenantA, w.AdminA, w.CustomerX, price: 0m, quantity: 1);

        using var pay = await PayAsync(w.TenantA, free, w.CustomerX, "sim_success", Key());
        using var cancel = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/orders/{free}/cancel", w.CustomerX);

        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.OK), (pay.StatusCode, cancel.StatusCode));
        Assert.Equal("payments.nothing_to_pay", await CodeAsync(pay));
        Assert.Equal(0L, await ScalarAsync(w.TenantA, "SELECT count(*) FROM payments.payments WHERE order_id = @id", free));
        Assert.Equal("cancelled", await TextAsync(w.TenantA, "SELECT status FROM orders.orders WHERE id = @id", free));
    }

    // ---- criterion 15 ----

    [Fact]
    public async Task A_card_number_is_refused_never_echoed_and_nothing_is_stored()
    {
        var w = await CreateWorldAsync();
        var order = await ReservedOrderAsync(w.TenantA, w.AdminA, w.CustomerX);

        using var card = await PayAsync(w.TenantA, order, w.CustomerX, "4242424242424242", Key());
        using var tokenWithCard = await PayAsync(w.TenantA, order, w.CustomerX, "pm_4242424242424242", Key());

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (card.StatusCode, tokenWithCard.StatusCode));
        Assert.Equal("payments.payment_method.invalid", await FieldCodeAsync(card));
        Assert.DoesNotContain("424242", await card.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(0L, await ScalarAsync(w.TenantA, "SELECT count(*) FROM payments.payments WHERE order_id = @id", order));
        Assert.Equal(0L, await ScalarAsync(w.TenantA, "SELECT count(*) FROM payments.idempotency_keys WHERE @id IS NOT NULL", order));
    }

    // ---- helpers ----

    private static string Key() => $"key-{Guid.NewGuid():N}";

    private WebApplicationFactory<Program> CreateFactory(Func<IPaymentProvider, IPaymentProvider> wrap) =>
        ApiFactory.CreateProduction(
            services =>
            {
                var original = services.Single(static d => d.ServiceType == typeof(PaymentProviders));
                services.Remove(original);
                services.AddSingleton(provider =>
                {
                    var adapters = (PaymentProviders)original.ImplementationFactory!(provider);
                    return new PaymentProviders(adapters.All.Select(wrap), adapters.Current.Name);
                });
            },
            connectionString: fixture.Database.AppConnectionString);

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

    // An order of the customer's, submitted and reserved: pending_payment.
    private async Task<Guid> ReservedOrderAsync(Guid tenant, string admin, string customer, decimal price = 1m, int quantity = 1)
    {
        var product = await StockedProductAsync(tenant, admin, price, quantity);
        var order = await CreateOrderAsync(tenant, customer, product, quantity);
        using var submitted = await SendAsync(HttpMethod.Post, $"/v1/tenants/{tenant}/orders/{order}/submit", customer);
        Assert.Equal("pending_payment", (await JsonAsync(submitted)).GetProperty("status").GetString());
        return order;
    }

    private async Task<Guid> StockedProductAsync(Guid tenant, string admin, decimal price, int onHand)
    {
        using var created = await SendAsync(HttpMethod.Post, $"/v1/tenants/{tenant}/products", admin,
            new { sku = $"SKU-{Guid.NewGuid():N}", name = "Widget", price = new { amount = price, currency = "EUR" } });
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

    private Task<HttpResponseMessage> PayAsync(Guid tenant, Guid order, string subject, string paymentMethod, string? key) =>
        PayAsync(Factory, tenant, order, subject, paymentMethod, key);

    private static async Task<HttpResponseMessage> PayAsync(WebApplicationFactory<Program> factory, Guid tenant, Guid order, string subject, string paymentMethod, string? key)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/tenants/{tenant}/orders/{order}/payments")
        {
            Content = JsonContent.Create(new { paymentMethod }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Shared.CreateToken(subject));
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
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

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"Expected success, got {(int)response.StatusCode}.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return json.RootElement.Clone();
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return json.RootElement.GetProperty("code").GetString();
    }

    private static async Task<string?> FieldCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return Assert.Single(json.RootElement.GetProperty("errors").EnumerateArray()).GetProperty("code").GetString();
    }

    private async Task<bool> TransitionDirectlyAsync(Guid tenant, Guid order, OrderStatus from, OrderStatus to)
    {
        var services = new ServiceCollection();
        services.AddOrdersModule(fixture.Database.AppConnectionString);
        services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenant));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IOrderStore>()
            .TryTransitionAsync(order, from, to, null, null, null, TestContext.Current.CancellationToken);
    }

    private sealed record OutboxRow(string Type, Guid Tenant, string Payload);

    private async Task<List<OutboxRow>> OutboxAsync(Guid tenant)
    {
        var rows = new List<OutboxRow>();
        await InTenantAsync(tenant, "SELECT type, tenant_id, payload::text FROM payments.outbox_messages WHERE tenant_id = @id", tenant, async command =>
        {
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                rows.Add(new OutboxRow(reader.GetString(0), reader.GetGuid(1), reader.GetString(2)));
            }

            return null;
        });
        return rows;
    }

    private async Task<long> ScalarAsync(Guid tenant, string sql, Guid id) =>
        Convert.ToInt64(await InTenantAsync(tenant, sql, id, static command => command.ExecuteScalarAsync(TestContext.Current.CancellationToken)), System.Globalization.CultureInfo.InvariantCulture);

    private async Task<string?> TextAsync(Guid tenant, string sql, Guid id) =>
        await InTenantAsync(tenant, sql, id, static command => command.ExecuteScalarAsync(TestContext.Current.CancellationToken)) as string;

    private Task<object?> ExecuteAsync(Guid tenant, string sql, Guid id) =>
        InTenantAsync(tenant, sql, id, static async command => (object?)await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

    // Reads as the application role inside the tenant's scope: the owner role cannot see the rows (FORCE RLS).
    private async Task<object?> InTenantAsync(Guid tenant, string sql, Guid id, Func<NpgsqlCommand, Task<object?>> run)
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
        command.Parameters.AddWithValue("id", id);
        var result = await run(command);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return result is DBNull ? null : result;
    }

    private sealed class CallLog
    {
        private readonly ConcurrentDictionary<Guid, int> _calls = new();

        public void Add(Guid paymentId) => _calls.AddOrUpdate(paymentId, 1, static (_, n) => n + 1);

        public int For(Guid paymentId) => _calls.GetValueOrDefault(paymentId);
    }

    private sealed class CountingProvider(IPaymentProvider inner, CallLog calls) : IPaymentProvider
    {
        public string Name => inner.Name;

        public Task<ProviderPaymentResult> CreateAsync(ProviderPaymentRequest request, CancellationToken cancellationToken)
        {
            calls.Add(request.PaymentId);
            return inner.CreateAsync(request, cancellationToken);
        }

        public Task<ProviderPaymentResult?> GetStatusAsync(ProviderStatusQuery query, CancellationToken cancellationToken) =>
            inner.GetStatusAsync(query, cancellationToken);

        public Task<ProviderRefundResult> RefundAsync(ProviderRefundRequest request, CancellationToken cancellationToken) =>
            inner.RefundAsync(request, cancellationToken);

        public Task<ProviderRefundResult?> GetRefundStatusAsync(ProviderRefundQuery query, CancellationToken cancellationToken) =>
            inner.GetRefundStatusAsync(query, cancellationToken);
    }

    private sealed class Gate
    {
        private int _calls;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => _calls;

        public void Enter()
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
        }
    }

    // Holds the provider call open until the test releases it.
    private sealed class GatedProvider(IPaymentProvider inner, Gate gate) : IPaymentProvider
    {
        public string Name => inner.Name;

        public async Task<ProviderPaymentResult> CreateAsync(ProviderPaymentRequest request, CancellationToken cancellationToken)
        {
            gate.Enter();
            await gate.Release.Task.WaitAsync(cancellationToken);
            return await inner.CreateAsync(request, cancellationToken);
        }

        public Task<ProviderPaymentResult?> GetStatusAsync(ProviderStatusQuery query, CancellationToken cancellationToken) =>
            inner.GetStatusAsync(query, cancellationToken);

        public Task<ProviderRefundResult> RefundAsync(ProviderRefundRequest request, CancellationToken cancellationToken) =>
            inner.RefundAsync(request, cancellationToken);

        public Task<ProviderRefundResult?> GetRefundStatusAsync(ProviderRefundQuery query, CancellationToken cancellationToken) =>
            inner.GetRefundStatusAsync(query, cancellationToken);
    }
}
