using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using TenantFlow.BuildingBlocks.Persistence;

namespace TenantFlow.Database.Tests.Support;

/// <summary>
/// The setup the payment tests share (SPEC-0006): two tenants with an administrator each and a customer in A, reserved
/// orders, payments through the real API, signed simulator webhooks, and reads as the application role inside a tenant.
/// </summary>
internal sealed class PaymentScenario(TenantDatabaseFixture fixture, WebApplicationFactory<Program> factory)
{
    public Guid TenantA { get; private set; }

    public Guid TenantB { get; private set; }

    public string AdminA { get; private set; } = string.Empty;

    public string CustomerA { get; private set; } = string.Empty;

    public string AdminB { get; private set; } = string.Empty;

    public static async Task<PaymentScenario> CreateAsync(TenantDatabaseFixture fixture, WebApplicationFactory<Program> factory)
    {
        var scenario = new PaymentScenario(fixture, factory);
        var suffix = Guid.NewGuid().ToString("N");
        scenario.TenantA = await fixture.Harness.CreateTenantAsync("Tenant A");
        scenario.TenantB = await fixture.Harness.CreateTenantAsync("Tenant B");
        (scenario.AdminA, scenario.CustomerA, scenario.AdminB) = ($"admin-a-{suffix}", $"customer-a-{suffix}", $"admin-b-{suffix}");
        await fixture.Harness.AddAsync(scenario.TenantA, scenario.AdminA, "tenant-admin", TestIssuer.Issuer);
        await fixture.Harness.AddAsync(scenario.TenantA, scenario.CustomerA, "customer", TestIssuer.Issuer);
        await fixture.Harness.AddAsync(scenario.TenantB, scenario.AdminB, "tenant-admin", TestIssuer.Issuer);
        return scenario;
    }

    /// <summary>A customer order of tenant A (or B's administrator's in B), submitted and reserved: <c>pending_payment</c>.</summary>
    public async Task<Guid> ReservedOrderAsync(Guid tenant, int onHand = 1, int quantity = 1)
    {
        var (admin, customer) = tenant == TenantA ? (AdminA, CustomerA) : (AdminB, AdminB);
        using var product = await SendAsync(HttpMethod.Post, $"/v1/tenants/{tenant}/products", admin,
            new { sku = $"SKU-{Guid.NewGuid():N}", name = "Widget", price = new { amount = 2m, currency = "EUR" } });
        var productId = Id(product);
        using var stocked = await SendAsync(HttpMethod.Post, $"/v1/tenants/{tenant}/products/{productId}/stock/adjustments", admin, new { delta = onHand, reason = "Stock count" });
        Assert.Equal(HttpStatusCode.OK, stocked.StatusCode);
        using var order = await SendAsync(HttpMethod.Post, $"/v1/tenants/{tenant}/orders", customer, new { lines = new[] { new { productId, quantity } } });
        var orderId = Id(order);
        using var submitted = await SendAsync(HttpMethod.Post, $"/v1/tenants/{tenant}/orders/{orderId}/submit", customer);
        Assert.Equal("pending_payment", (await JsonAsync(submitted)).GetProperty("status").GetString());
        return orderId;
    }

    /// <summary>Pays the order with a simulator token and returns the payment id.</summary>
    public async Task<Guid> PayAsync(Guid tenant, Guid order, string paymentMethod)
    {
        var customer = tenant == TenantA ? CustomerA : AdminB;
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/tenants/{tenant}/orders/{order}/payments") { Content = JsonContent.Create(new { paymentMethod }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Shared.CreateToken(customer));
        request.Headers.Add("Idempotency-Key", $"key-{Guid.NewGuid():N}");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>Delivers a webhook body with the given signature header (or none).</summary>
    public async Task<HttpResponseMessage> DeliverAsync(string body, string? signature, string path = SimulatorWebhooks.Path)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (signature is not null)
        {
            request.Headers.TryAddWithoutValidation(SimulatorWebhooks.SignatureHeader, signature);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Delivers a correctly signed webhook, signed now.</summary>
    public Task<HttpResponseMessage> DeliverSignedAsync(string body) => DeliverAsync(body, SimulatorWebhooks.Sign(body, DateTimeOffset.UtcNow));

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string subject, object? body = null)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Shared.CreateToken(subject));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"Expected success, got {(int)response.StatusCode}.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return json.RootElement.Clone();
    }

    public static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return json.RootElement.GetProperty("code").GetString();
    }

    /// <summary>One value read as the application role inside <paramref name="tenant"/> (FORCE RLS hides rows from the owner role).</summary>
    public async Task<object?> ScalarAsync(Guid tenant, string sql, Guid id)
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
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return result is DBNull ? null : result;
    }

    public async Task<long> CountAsync(Guid tenant, string sql, Guid id) =>
        Convert.ToInt64(await ScalarAsync(tenant, sql, id), System.Globalization.CultureInfo.InvariantCulture);

    public async Task<string?> TextAsync(Guid tenant, string sql, Guid id) => await ScalarAsync(tenant, sql, id) as string;

    private static Guid Id(HttpResponseMessage created)
    {
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return Guid.Parse(created.Headers.Location!.ToString().Split('/')[^1]);
    }
}
