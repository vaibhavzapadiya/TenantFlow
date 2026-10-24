using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TenantFlow.Database.Tests.Catalog;

/// <summary>
/// SPEC-0003 criteria 5 to 10 and 17 against real PostgreSQL, through the real host with real tenant-scheme tokens.
/// Two tenants, A and B, each with an administrator, and a customer in A.
/// </summary>
public sealed class ProductEndpointTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
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

    // ---- criterion 5 ----

    [Fact]
    public async Task An_administrator_creates_a_product_and_reads_it_back()
    {
        var w = await CreateWorldAsync();

        using var created = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/products", w.AdminA, NewProduct("ABC-1"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var location = created.Headers.Location!.ToString();
        Assert.StartsWith($"/v1/tenants/{w.TenantA}/products/", location, StringComparison.Ordinal);

        using var read = await SendAsync(HttpMethod.Get, location, w.AdminA);
        var body = await JsonAsync(read);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(("ABC-1", "Widget", 12.5m, "EUR", "active"),
            (body.GetProperty("sku").GetString(), body.GetProperty("name").GetString(), body.GetProperty("price").GetProperty("amount").GetDecimal(),
             body.GetProperty("price").GetProperty("currency").GetString(), body.GetProperty("status").GetString()));
        Assert.NotNull(read.Headers.ETag);
    }

    // ---- criterion 6 ----

    [Fact]
    public async Task A_sku_is_unique_within_a_tenant_ignoring_case_but_not_across_tenants()
    {
        var w = await CreateWorldAsync();
        using var first = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/products", w.AdminA, NewProduct("Same-Sku"));

        using var duplicate = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/products", w.AdminA, NewProduct("SAME-sku"));
        using var otherTenant = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantB}/products", w.AdminB, NewProduct("Same-Sku"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("catalog.sku_taken", (await JsonAsync(duplicate)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, otherTenant.StatusCode);
    }

    // ---- criterion 7 ----

    [Fact]
    public async Task Invalid_input_is_a_400_with_field_errors_and_writes_nothing()
    {
        var w = await CreateWorldAsync();

        using var response = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/products", w.AdminA,
            new { sku = " ", name = "Widget", price = new { amount = -1, currency = "EURO" } });
        using var list = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}/products", w.AdminA);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var fields = (await JsonAsync(response)).GetProperty("errors").EnumerateArray().Select(static e => e.GetProperty("code").GetString()).ToList();
        Assert.Contains("catalog.sku.required", fields);
        Assert.Contains("catalog.price.amount_invalid", fields);
        Assert.Empty((await JsonAsync(list)).GetProperty("items").EnumerateArray());
    }

    // ---- criterion 8 ----

    [Fact]
    public async Task An_edit_needs_the_current_etag_and_a_stale_one_changes_nothing()
    {
        var w = await CreateWorldAsync();
        using var created = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/products", w.AdminA, NewProduct("EDIT-1"));
        var location = created.Headers.Location!.ToString();
        var firstTag = created.Headers.ETag!.Tag;

        using var edited = await SendAsync(HttpMethod.Put, location, w.AdminA, Edit("Renamed", "archived"), firstTag);
        using var stale = await SendAsync(HttpMethod.Put, location, w.AdminA, Edit("Lost update", "active"), firstTag);
        using var missing = await SendAsync(HttpMethod.Put, location, w.AdminA, Edit("No tag", "active"));
        using var after = await SendAsync(HttpMethod.Get, location, w.AdminA);

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.NotEqual(firstTag, edited.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("catalog.version_conflict", (await JsonAsync(stale)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("catalog.if_match_required", (await JsonAsync(missing)).GetProperty("code").GetString());
        var body = await JsonAsync(after);
        Assert.Equal(("Renamed", "archived", "EDIT-1"), (body.GetProperty("name").GetString(), body.GetProperty("status").GetString(), body.GetProperty("sku").GetString()));

        // RFC 9110 If-Match forms: a weak tag never matches (strong comparison), a list matches if any tag does, * matches any.
        var current = after.Headers.ETag!.Tag;
        using var weak = await SendAsync(HttpMethod.Put, location, w.AdminA, Edit("Weak", "active"), $"W/{current}");
        using var list = await SendAsync(HttpMethod.Put, location, w.AdminA, Edit("Listed", "active"), $"\"1\", {current}");
        using var any = await SendAsync(HttpMethod.Put, location, w.AdminA, Edit("Any", "active"), "*");
        Assert.Equal((HttpStatusCode.PreconditionFailed, HttpStatusCode.OK, HttpStatusCode.OK), (weak.StatusCode, list.StatusCode, any.StatusCode));
    }

    // ---- criterion 9 ----

    [Fact]
    public async Task A_customer_sees_active_products_only_and_cannot_write()
    {
        var w = await CreateWorldAsync();
        using var active = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/products", w.AdminA, NewProduct("ACTIVE-1"));
        using var toArchive = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/products", w.AdminA, NewProduct("ARCHIVED-1"));
        var archivedLocation = toArchive.Headers.Location!.ToString();
        using var archived = await SendAsync(HttpMethod.Put, archivedLocation, w.AdminA, Edit("Gone", "archived"), toArchive.Headers.ETag!.Tag);

        using var list = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}/products?status=archived", w.CustomerA);
        using var getActive = await SendAsync(HttpMethod.Get, active.Headers.Location!.ToString(), w.CustomerA);
        using var getArchived = await SendAsync(HttpMethod.Get, archivedLocation, w.CustomerA);
        using var create = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/products", w.CustomerA, NewProduct("CUSTOMER-1"));
        using var update = await SendAsync(HttpMethod.Put, active.Headers.Location!.ToString(), w.CustomerA, Edit("Hijacked", "active"), active.Headers.ETag!.Tag);

        Assert.Equal(["ACTIVE-1"], (await JsonAsync(list)).GetProperty("items").EnumerateArray().Select(static i => i.GetProperty("sku").GetString()).ToList());
        Assert.Equal(HttpStatusCode.OK, getActive.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, getArchived.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
    }

    // ---- criterion 10: the cross-tenant denial ----

    [Fact]
    public async Task Another_tenants_products_cannot_be_read_listed_or_changed()
    {
        var w = await CreateWorldAsync();
        using var bProduct = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantB}/products", w.AdminB, NewProduct("B-ONLY"));
        var id = bProduct.Headers.Location!.ToString().Split('/')[^1];

        // Tenant A's administrator names tenant A in the route but tenant B's product id.
        using var get = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}/products/{id}", w.AdminA);
        using var put = await SendAsync(HttpMethod.Put, $"/v1/tenants/{w.TenantA}/products/{id}", w.AdminA, Edit("Hijacked", "archived"), bProduct.Headers.ETag!.Tag);
        using var list = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}/products", w.AdminA);
        using var own = await SendAsync(HttpMethod.Get, bProduct.Headers.Location!.ToString(), w.AdminB);

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal("catalog.product_not_found", (await JsonAsync(get)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Empty((await JsonAsync(list)).GetProperty("items").EnumerateArray());
        var untouched = await JsonAsync(own);
        Assert.Equal(("Widget", "active"), (untouched.GetProperty("name").GetString(), untouched.GetProperty("status").GetString()));
        Assert.Equal(bProduct.Headers.ETag!.Tag, own.Headers.ETag!.Tag);
    }

    // ---- criterion 17 ----

    [Fact]
    public async Task Paging_by_cursor_returns_every_product_once_even_when_rows_arrive_between_pages()
    {
        var w = await CreateWorldAsync();
        foreach (var sku in new[] { "P-1", "P-2", "P-3", "P-4", "P-5" })
        {
            using var _ = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/products", w.AdminA, NewProduct(sku));
        }

        var seen = new List<string?>();
        using var firstPage = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}/products?limit=2", w.AdminA);
        var page = await JsonAsync(firstPage);
        seen.AddRange(page.GetProperty("items").EnumerateArray().Select(static i => i.GetProperty("sku").GetString()));

        // A product that sorts before the cursor, and one after it, arrive between the pages.
        using var _1 = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/products", w.AdminA, NewProduct("A-EARLY"));
        using var _2 = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/products", w.AdminA, NewProduct("P-2A"));

        while (page.GetProperty("nextCursor").GetString() is { } cursor)
        {
            using var next = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}/products?limit=2&cursor={Uri.EscapeDataString(cursor)}", w.AdminA);
            page = await JsonAsync(next);
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(static i => i.GetProperty("sku").GetString()));
        }

        Assert.Equal(["P-1", "P-2", "P-2A", "P-3", "P-4", "P-5"], seen);

        using var badCursor = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}/products?cursor=not-a-cursor", w.AdminA);
        using var badLimit = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}/products?limit=101", w.AdminA);
        Assert.Equal("catalog.cursor_invalid", (await JsonAsync(badCursor)).GetProperty("code").GetString());
        Assert.Equal("catalog.limit_invalid", (await JsonAsync(badLimit)).GetProperty("code").GetString());
    }

    // ---- helpers ----

    // ---- SPEC-0008 criterion 1: search ----

    [Fact]
    public async Task Search_finds_every_word_in_name_or_sku_with_a_prefix_last_word_inside_the_tenant_only()
    {
        var w = await CreateWorldAsync();
        foreach (var (sku, name) in new[] { ("MUG-1", "Blue Mug"), ("PLATE-1", "Blue Plate"), ("MUG-2", "Red Mug"), ("MUG-3", "Blue Mug Archived") })
        {
            using var created = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantA}/products", w.AdminA, new { sku, name, price = new { amount = 5m, currency = "EUR" } });
            if (sku == "MUG-3")
            {
                using var archived = await SendAsync(HttpMethod.Put, created.Headers.Location!.ToString(), w.AdminA, Edit(name, "archived"), created.Headers.ETag!.Tag);
            }
        }

        using var other = await SendAsync(HttpMethod.Post, $"/v1/tenants/{w.TenantB}/products", w.AdminB, new { sku = "MUG-B", name = "Blue Mug", price = new { amount = 5m, currency = "EUR" } });
        using var both = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}/products?q=blue%20mu", w.AdminA);
        using var bySku = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}/products?q=plate-1", w.AdminA);
        using var byCustomer = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}/products?q=mug", w.CustomerA);
        using var operators = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}/products?q=%27%7C!%26", w.AdminA);
        using var tooLong = await SendAsync(HttpMethod.Get, $"/v1/tenants/{w.TenantA}/products?q={new string('a', 101)}", w.AdminA);

        Assert.Equal(["MUG-1", "MUG-3"], await SkusAsync(both));
        Assert.Equal(["PLATE-1"], await SkusAsync(bySku));
        Assert.Equal(["MUG-1", "MUG-2"], await SkusAsync(byCustomer));
        Assert.Empty(await SkusAsync(operators));
        Assert.Equal((HttpStatusCode.BadRequest, "catalog.search_invalid"), (tooLong.StatusCode, (await JsonAsync(tooLong)).GetProperty("code").GetString()));
    }

    private static async Task<List<string?>> SkusAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await JsonAsync(response)).GetProperty("items").EnumerateArray().Select(static i => i.GetProperty("sku").GetString())];
    }

    private static object NewProduct(string sku) => new { sku, name = "Widget", price = new { amount = 12.5m, currency = "EUR" } };

    private static object Edit(string name, string status) => new { name, price = new { amount = 15m, currency = "EUR" }, status };

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string subject, object? body = null, string? ifMatch = null)
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Shared.CreateToken(subject));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone();
}
