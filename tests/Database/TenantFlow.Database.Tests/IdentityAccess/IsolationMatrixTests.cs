using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TenantFlow.Api.Endpoints.V1.TenantAccess;

namespace TenantFlow.Database.Tests.IdentityAccess;

/// <summary>
/// The M2 gate (SPEC-0002 criteria 12 to 14, 18, 22 and 23 together): the isolation matrix. The routes are read from the
/// host's own route table (every route that declares a tenant permission), so a tenant route added later is covered
/// without anyone remembering to add it here. Each route is called by every kind of caller and must answer exactly as
/// the boundary says, leave the data untouched when it denies, and count the denial once with the right labels.
/// </summary>
public sealed class IsolationMatrixTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>, IAsyncLifetime
{
    private const string Counter = "tenantflow.auth.denials";

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

    private sealed record TenantRoute(string Method, string Pattern, string Permission);

    private sealed record Outcome(string Actor, string Route, HttpStatusCode Status, string? Code, string Shape);

    [Fact]
    public async Task Every_tenant_route_answers_every_kind_of_caller_exactly_as_the_boundary_says()
    {
        var harness = fixture.Harness;
        var suffix = Guid.NewGuid().ToString("N");
        var a = await harness.CreateTenantAsync("A");
        var b = await harness.CreateTenantAsync("B");
        var suspended = await harness.CreateTenantAsync("S");
        string adminA = $"admin-a-{suffix}", customerA = $"customer-a-{suffix}", removedA = $"removed-a-{suffix}", adminB = $"admin-b-{suffix}", memberS = $"member-s-{suffix}";
        await harness.AddAsync(a, adminA, "tenant-admin", TestIssuer.Issuer);
        await harness.AddAsync(a, customerA, "customer", TestIssuer.Issuer);
        var gone = await harness.AddAsync(a, removedA, "customer", TestIssuer.Issuer);
        await harness.RemoveAsync(a, gone.Id);
        await harness.AddAsync(b, adminB, "tenant-admin", TestIssuer.Issuer);
        await harness.AddAsync(suspended, memberS, "tenant-admin", TestIssuer.Issuer);
        await harness.SuspendTenantAsync(suspended);

        var routes = TenantRoutes();
        Assert.Equal(32, routes.Count);
        var failures = new List<string>();
        var shapes = new Dictionary<string, List<string>>();
        using var meter = new MeterCapture(Counter);

        foreach (var route in routes)
        {
            var label = $"{route.Method} {route.Pattern}";

            // ---- callers who must be refused, with the data left exactly as it was ----
            await Denied("anonymous", label, route, a, token: null, HttpStatusCode.Unauthorized, "authentication.required", "tenant", "no_token");
            await Denied("platform operator", label, route, a, TestIssuer.Platform.CreateOperatorToken("operator-a"), HttpStatusCode.Unauthorized, "authentication.required", "tenant", "invalid_token");
            await Denied("non-member of A", label, route, a, TestIssuer.Shared.CreateToken(adminB), HttpStatusCode.NotFound, "tenant.not_found", "tenant", "not_a_member");
            await Denied("removed member of A", label, route, a, TestIssuer.Shared.CreateToken(removedA), HttpStatusCode.NotFound, "tenant.not_found", "tenant", "not_a_member");
            await Denied("admin of A, unknown tenant", label, route, Guid.CreateVersion7(), TestIssuer.Shared.CreateToken(adminA), HttpStatusCode.NotFound, "tenant.not_found", "tenant", "not_a_member");
            await Denied("admin of A, malformed id", label, route, "not-a-guid", TestIssuer.Shared.CreateToken(adminA), HttpStatusCode.NotFound, "tenant.not_found", "tenant", "not_a_member");
            await Denied("member of a suspended tenant", label, route, suspended, TestIssuer.Shared.CreateToken(memberS), HttpStatusCode.Forbidden, "membership.tenant_suspended", "tenant", "tenant_suspended");

            // ---- a customer holds exactly tenant.read, catalog.read, orders.place and subscriptions.subscribe ----
            if (route.Permission is "tenant.read" or "catalog.read" or "orders.place" or "subscriptions.subscribe")
            {
                var allowed = await CallAsync(route, a, TestIssuer.Shared.CreateToken(customerA));
                // The handler may still answer 400 or 404 (a product or order that does not exist); only the boundary must let the customer in.
                if (allowed.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || allowed.Code == "tenant.not_found" || (route.Permission == "tenant.read" && allowed.Status != HttpStatusCode.OK))
                {
                    failures.Add($"customer of A on {label}: stopped by the boundary ({(int)allowed.Status} {allowed.Code})");
                }
            }
            else
            {
                await Denied("customer of A", label, route, a, TestIssuer.Shared.CreateToken(customerA), HttpStatusCode.Forbidden, "membership.permission_denied", "tenant", "permission_denied");
            }

            // ---- an administrator of the tenant is not stopped by the boundary (the handler may still answer 404 or 409) ----
            var admin = await CallAsync(route, a, TestIssuer.Shared.CreateToken(adminA));
            if (admin.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || admin.Code == "tenant.not_found")
            {
                failures.Add($"admin of A on {label}: stopped by the boundary ({(int)admin.Status} {admin.Code})");
            }
        }

        // ---- a denial for a tenant that exists looks exactly like a denial for one that does not ----
        foreach (var (route, list) in shapes)
        {
            if (list.Distinct(StringComparer.Ordinal).Count() != 1)
            {
                failures.Add($"{route}: not-found answers differ between a real tenant, an unknown id, and a malformed id: {string.Join(" / ", list.Distinct(StringComparer.Ordinal))}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        async Task Denied(string actor, string label, TenantRoute route, object tenant, string? token, HttpStatusCode status, string? expectedCode, string scheme, string reason)
        {
            var before = await SnapshotAsync();
            meter.Clear();
            var outcome = await CallAsync(route, tenant, token);
            var counted = meter.Measurements;
            if (outcome.Status != status || outcome.Code != expectedCode)
            {
                failures.Add($"{actor} on {label}: expected {(int)status} {expectedCode}, got {(int)outcome.Status} {outcome.Code}");
            }

            if (await SnapshotAsync() != before)
            {
                failures.Add($"{actor} on {label}: a denied request changed data");
            }

            if (counted.Count != 1 || counted[0].Tags.Count != 2 || counted[0].Tags.GetValueOrDefault("scheme") != scheme || counted[0].Tags.GetValueOrDefault("reason") != reason)
            {
                failures.Add($"{actor} on {label}: expected one denial counted as {scheme}/{reason}, got [{string.Join("; ", counted.Select(static m => string.Join(",", m.Tags.Select(static t => $"{t.Key}={t.Value}"))))}]");
            }

            if (status == HttpStatusCode.NotFound && actor is "non-member of A" or "admin of A, unknown tenant" or "admin of A, malformed id")
            {
                if (!shapes.TryGetValue(label, out var list))
                {
                    shapes[label] = list = [];
                }

                list.Add(outcome.Shape);
            }
        }
    }

    // ---- helpers ----

    private List<TenantRoute> TenantRoutes() =>
    [
        .. Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(static route => route.Metadata.GetMetadata<TenantPermissionRequirement>() is not null)
            .SelectMany(static route => (route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(method =>
                new TenantRoute(method, route.RoutePattern.RawText!.TrimEnd('/'), route.Metadata.GetMetadata<TenantPermissionRequirement>()!.Permission))),
    ];

    private async Task<Outcome> CallAsync(TenantRoute route, object tenant, string? token)
    {
        var path = route.Pattern
            .Replace("{tenantId}", tenant.ToString(), StringComparison.Ordinal)
            .Replace("{membershipId}", Guid.CreateVersion7().ToString(), StringComparison.Ordinal)
            .Replace("{productId}", Guid.CreateVersion7().ToString(), StringComparison.Ordinal)
            .Replace("{orderId}", Guid.CreateVersion7().ToString(), StringComparison.Ordinal)
            .Replace("{subscriptionId}", Guid.CreateVersion7().ToString(), StringComparison.Ordinal)
            .Replace("{importId}", Guid.CreateVersion7().ToString(), StringComparison.Ordinal)
            .Replace("{exportId}", Guid.CreateVersion7().ToString(), StringComparison.Ordinal);
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(route.Method), path);
        if (route.Pattern.EndsWith("/imports/products", StringComparison.Ordinal))
        {
            // An upload route only matches a multipart body; any other body is refused by routing (415) for every caller.
            request.Content = new MultipartFormDataContent { { new StringContent("sku"), "file", "probe.csv" } };
        }
        else if (route.Method is "POST" or "PUT")
        {
            request.Content = new StringContent("""{"subject":"matrix-probe","role":"customer"}""", System.Text.Encoding.UTF8, "application/json");
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        string? code = null;
        var shape = $"{(int)response.StatusCode}";
        if (response.Content.Headers.ContentType?.MediaType == "application/problem+json")
        {
            using var problem = JsonDocument.Parse(text);
            var root = problem.RootElement;
            code = root.GetProperty("code").GetString();
            shape = $"{(int)response.StatusCode}|{code}|{root.GetProperty("title").GetString()}|{root.GetProperty("type").GetString()}";
        }

        return new Outcome(string.Empty, route.Pattern, response.StatusCode, code, shape);
    }

    // Every membership and every audit row in the database: a denied request may change none of it.
    private async Task<string> SnapshotAsync()
    {
        var rows = await MigratorRun.QueryAsync<string>(
            fixture.Database.OwnerConnectionString,
            """
            SELECT coalesce((SELECT md5(string_agg(m::text, ',' ORDER BY m.id)) FROM identity_access.memberships m), '') || '|' ||
                   (SELECT count(*) FROM identity_access.membership_audit)
            """);
        return Assert.Single(rows);
    }
}
