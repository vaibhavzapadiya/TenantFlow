using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using TenantFlow.BuildingBlocks.Persistence;

namespace TenantFlow.Database.Tests.Subscriptions;

/// <summary>
/// SPEC-0007 criteria 1 to 4 against real PostgreSQL, through the real host. Tenant A has an administrator and two
/// customers; tenant B an administrator. Subscribing charges nothing in the request: what it leaves behind is the
/// subscription and its renewal row, read here as the application role inside the tenant.
/// </summary>
public sealed class SubscriptionEndpointTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
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

    // ---- criterion 1 ----

    [Fact]
    public async Task An_admin_creates_plans_that_members_list_and_a_customer_cannot_create_one()
    {
        var w = await CreateWorldAsync();

        var plan = await CreatePlanAsync(w.TenantA, w.AdminA, trialDays: 14, amount: 9.99m);
        using var byCustomer = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/plans", w.CustomerX, PlanBody(0, 5m));
        using var zero = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/plans", w.AdminA, PlanBody(0, 0m));
        using var listed = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}/plans", w.CustomerX);

        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.BadRequest), (byCustomer.StatusCode, zero.StatusCode));
        Assert.Equal("subscriptions.plan.amount_invalid", await FieldCodeAsync(zero));
        var item = Assert.Single((await JsonAsync(listed)).GetProperty("items").EnumerateArray());
        Assert.Equal((plan, 9.99m, "EUR", "month", 14), (item.GetProperty("id").GetGuid(), item.GetProperty("price").GetProperty("amount").GetDecimal(),
            item.GetProperty("price").GetProperty("currency").GetString(), item.GetProperty("interval").GetString(), item.GetProperty("trialDays").GetInt32()));
    }

    // ---- criterion 2 ----

    [Fact]
    public async Task Subscribing_starts_a_trial_or_an_incomplete_subscription_with_its_renewal_row_and_a_second_live_one_is_refused()
    {
        var w = await CreateWorldAsync();
        var trialPlan = await CreatePlanAsync(w.TenantA, w.AdminA, trialDays: 14);
        var directPlan = await CreatePlanAsync(w.TenantA, w.AdminA, trialDays: 0);

        using var trial = await SubscribeAsync(w.TenantA, w.CustomerX, trialPlan);
        using var direct = await SubscribeAsync(w.TenantA, w.CustomerX, directPlan);
        using var again = await SubscribeAsync(w.TenantA, w.CustomerX, trialPlan);
        using var otherMember = await SubscribeAsync(w.TenantA, w.CustomerY, trialPlan);

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.Conflict, HttpStatusCode.Created),
            (trial.StatusCode, direct.StatusCode, again.StatusCode, otherMember.StatusCode));
        Assert.Equal("subscriptions.already_subscribed", await CodeAsync(again));
        var trialing = await JsonAsync(trial);
        var incomplete = await JsonAsync(direct);
        Assert.Equal(("trialing", "incomplete"), (trialing.GetProperty("status").GetString(), incomplete.GetProperty("status").GetString()));
        var trialId = trialing.GetProperty("id").GetGuid();
        var directId = incomplete.GetProperty("id").GetGuid();
        var created = trialing.GetProperty("createdAt").GetDateTimeOffset();
        Assert.Equal(created.AddDays(14), trialing.GetProperty("currentPeriodEnd").GetDateTimeOffset());

        // The renewal row is due at the period end: the trial's end, or at once without a trial (ADR-0023 decision 1).
        Assert.Equal(1L, await ScalarAsync(w.TenantA, "SELECT count(*) FROM subscriptions.workflow_timers WHERE kind = 'subscriptions.renew' AND subject_id = @id AND due_at = (SELECT period_end FROM subscriptions.subscriptions WHERE id = @id)", trialId));
        Assert.Equal(1L, await ScalarAsync(w.TenantA, "SELECT count(*) FROM subscriptions.workflow_timers WHERE kind = 'subscriptions.renew' AND subject_id = @id AND due_at = (SELECT created_at FROM subscriptions.subscriptions WHERE id = @id)", directId));
        Assert.Equal(2L, await ScalarAsync(w.TenantA, "SELECT count(*) FROM subscriptions.subscriptions WHERE plan_id = @id", trialPlan));
    }

    [Fact]
    public async Task Two_concurrent_subscribes_of_one_member_to_one_plan_leave_exactly_one_subscription_and_one_renewal_row()
    {
        var w = await CreateWorldAsync();
        var plan = await CreatePlanAsync(w.TenantA, w.AdminA, trialDays: 0);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => SubscribeAsync(w.TenantA, w.CustomerX, plan)));

        Assert.Equal(1, responses.Count(static r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, responses.Count(static r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1L, await ScalarAsync(w.TenantA, "SELECT count(*) FROM subscriptions.subscriptions WHERE plan_id = @id", plan));
        Assert.Equal(1L, await ScalarAsync(w.TenantA, "SELECT count(*) FROM subscriptions.workflow_timers t JOIN subscriptions.subscriptions s ON s.id = t.subject_id WHERE s.plan_id = @id", plan));
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task A_card_number_is_refused_never_echoed_and_nothing_is_stored()
    {
        var w = await CreateWorldAsync();
        var plan = await CreatePlanAsync(w.TenantA, w.AdminA, trialDays: 0);

        using var card = await SubscribeAsync(w.TenantA, w.CustomerX, plan, "pm_4242_4242_4242_4242");
        using var unknownPlan = await SubscribeAsync(w.TenantA, w.CustomerX, Guid.CreateVersion7());

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.NotFound), (card.StatusCode, unknownPlan.StatusCode));
        Assert.Equal("subscriptions.payment_method.invalid", await FieldCodeAsync(card));
        Assert.Equal("subscriptions.plan_not_found", await CodeAsync(unknownPlan));
        Assert.DoesNotContain("4242", await card.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(0L, await ScalarAsync(w.TenantA, "SELECT count(*) FROM subscriptions.subscriptions WHERE plan_id = @id", plan));
    }

    // ---- criterion 3 ----

    [Fact]
    public async Task Only_the_subscriber_and_a_manager_see_or_cancel_a_subscription_and_cancel_follows_the_period_rules()
    {
        var w = await CreateWorldAsync();
        var trialPlan = await CreatePlanAsync(w.TenantA, w.AdminA, trialDays: 14);
        var directPlan = await CreatePlanAsync(w.TenantA, w.AdminA, trialDays: 0);
        var trial = Id(await SubscribeAsync(w.TenantA, w.CustomerX, trialPlan));
        var incomplete = Id(await SubscribeAsync(w.TenantA, w.CustomerX, directPlan));

        using var byOwner = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}/subscriptions/{trial}", w.CustomerX);
        using var byAdmin = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}/subscriptions/{trial}", w.AdminA);
        using var byOther = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}/subscriptions/{trial}", w.CustomerY);
        using var cancelByOther = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/subscriptions/{trial}/cancel", w.CustomerY);
        using var cancelTrial = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/subscriptions/{trial}/cancel", w.CustomerX);
        using var cancelTrialAgain = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/subscriptions/{trial}/cancel", w.AdminA);
        using var cancelIncomplete = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/subscriptions/{incomplete}/cancel", w.AdminA);
        using var cancelIncompleteAgain = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/subscriptions/{incomplete}/cancel", w.CustomerX);

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.NotFound), (byOwner.StatusCode, byAdmin.StatusCode, byOther.StatusCode, cancelByOther.StatusCode));
        Assert.Equal(("subscriptions.subscription_not_found", "subscriptions.subscription_not_found"), (await CodeAsync(byOther), await CodeAsync(cancelByOther)));
        var pending = await JsonAsync(cancelTrial);
        Assert.Equal(("trialing", true), (pending.GetProperty("status").GetString(), pending.GetProperty("cancelAtPeriodEnd").GetBoolean()));
        Assert.Equal(HttpStatusCode.OK, cancelTrialAgain.StatusCode);
        Assert.Equal("cancelled", (await JsonAsync(cancelIncomplete)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, cancelIncompleteAgain.StatusCode);
        Assert.Equal("subscriptions.invalid_transition", await CodeAsync(cancelIncompleteAgain));
        Assert.Equal("requested", await TextAsync(w.TenantA, "SELECT cancel_reason FROM subscriptions.subscriptions WHERE id = @id", incomplete));
    }

    // ---- criterion 4 ----

    [Fact]
    public async Task Another_tenant_can_neither_see_nor_cancel_nor_subscribe_to_this_tenants_plans_and_subscriptions()
    {
        var w = await CreateWorldAsync();
        var plan = await CreatePlanAsync(w.TenantA, w.AdminA, trialDays: 14);
        var subscription = Id(await SubscribeAsync(w.TenantA, w.CustomerX, plan));

        // Tenant B's administrator, in tenant B's own routes, names tenant A's ids.
        using var read = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantB}/subscriptions/{subscription}", w.AdminB);
        using var cancel = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantB}/subscriptions/{subscription}/cancel", w.AdminB);
        using var subscribe = await SubscribeAsync(w.TenantB, w.AdminB, plan);
        using var plans = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantB}/plans", w.AdminB);

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound), (read.StatusCode, cancel.StatusCode, subscribe.StatusCode));
        Assert.Equal(("subscriptions.subscription_not_found", "subscriptions.plan_not_found"), (await CodeAsync(read), await CodeAsync(subscribe)));
        Assert.Empty((await JsonAsync(plans)).GetProperty("items").EnumerateArray());
        Assert.Equal(("trialing", false), (await TextAsync(w.TenantA, "SELECT status FROM subscriptions.subscriptions WHERE id = @id", subscription),
            await ScalarAsync(w.TenantA, "SELECT count(*) FROM subscriptions.subscriptions WHERE id = @id AND cancel_at_period_end", subscription) == 1));
        Assert.Equal(0L, await ScalarAsync(w.TenantB, "SELECT count(*) FROM subscriptions.subscriptions WHERE plan_id = @id", plan));
    }

    // ---- helpers ----

    private static object PlanBody(int trialDays, decimal amount) =>
        new { name = "Pro", price = new { amount, currency = "EUR" }, interval = "month", trialDays };

    private async Task<Guid> CreatePlanAsync(Guid tenant, string admin, int trialDays, decimal amount = 9.99m)
    {
        using var created = await SendAsync(HttpMethod.Post, $"/v1/tenants/{tenant}/plans", admin, PlanBody(trialDays, amount));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await JsonAsync(created)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> SubscribeAsync(Guid tenant, string subject, Guid plan, string paymentMethod = "sim_success") =>
        SendAsync(HttpMethod.Post, $"/v1/tenants/{tenant}/subscriptions", subject, new { planId = plan, paymentMethod });

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

    private static Guid Id(HttpResponseMessage created)
    {
        using (created)
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            return Guid.Parse(created.Headers.Location!.ToString().Split('/')[^1]);
        }
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

    private async Task<long> ScalarAsync(Guid tenant, string sql, Guid id) =>
        Convert.ToInt64(await InTenantAsync(tenant, sql, id), System.Globalization.CultureInfo.InvariantCulture);

    private async Task<string?> TextAsync(Guid tenant, string sql, Guid id) =>
        await InTenantAsync(tenant, sql, id) as string;

    // Reads as the application role inside the tenant's scope: the owner role cannot see the rows (FORCE RLS). The timer
    // table is not under RLS, so a query that reads it joins or compares with a subscription of the tenant.
    private async Task<object?> InTenantAsync(Guid tenant, string sql, Guid id)
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
}
