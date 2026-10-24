using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace TenantFlow.Database.Tests.Persistence;

/// <summary>
/// SPEC-0010 criterion 2 (decision 6): a request that reads the database has a span per database command in its trace,
/// with the statement text and without parameter values. A product is created with a planted SKU and name and then
/// searched for by them; neither value may appear in any database span, while the statements must.
/// </summary>
public sealed class DatabaseTelemetryTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>
{
    private const string Planted = "plantedvalue9c41";

    [Fact]
    public async Task Database_command_spans_carry_the_statement_but_no_parameter_value()
    {
        using var telemetry = new TelemetryCapture();
        // A middleware in front of routing keeps test request spans reliably exported (TelemetryTests in the Api tests).
        await using var factory = ApiFactory.CreateProduction(
            services => services.AddSingleton<IStartupFilter, PassThroughStartupFilter>(),
            connectionString: fixture.Database.AppConnectionString,
            telemetry: telemetry);
        var tenant = await fixture.Harness.CreateTenantAsync("Telemetry");
        var admin = $"admin-{Guid.NewGuid():N}";
        await fixture.Harness.AddAsync(tenant, admin, "tenant-admin", TestIssuer.Issuer);
        using var client = factory.CreateClient();

        using var created = await SendAsync(client, HttpMethod.Post, $"/v1/tenants/{tenant}/products", admin, new { sku = $"SKU-{Planted}", name = $"Name {Planted}", price = new { amount = 5m, currency = "EUR" } });
        using var search = await SendAsync(client, HttpMethod.Get, $"/v1/tenants/{tenant}/products?q={Planted}", admin);
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.OK), (created.StatusCode, search.StatusCode));
        var request = await telemetry.WaitForSpanAsync(
            span => span.Kind == ActivityKind.Server && span.Tags.GetValueOrDefault("url.path") == $"/v1/tenants/{tenant}/products" && span.Tags.GetValueOrDefault("http.request.method") == "GET",
            TestContext.Current.CancellationToken);
        // A command's span ends before the request's span does, so every one of them is captured by now.
        var databaseSpans = telemetry.Spans.Where(IsDatabaseSpan).ToList();
        var searchSpans = databaseSpans.Where(span => span.TraceId == request.TraceId).ToList();
        Assert.Contains(searchSpans, span => Statement(span)?.Contains("SELECT", StringComparison.OrdinalIgnoreCase) == true);
        Assert.All(databaseSpans, span => Assert.DoesNotContain(span.Tags, tag => $"{tag.Value}".Contains(Planted, StringComparison.OrdinalIgnoreCase)));
        Assert.All(databaseSpans, span => Assert.DoesNotContain(Planted, span.DisplayName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDatabaseSpan(CapturedSpan span) =>
        span.Kind == ActivityKind.Client && span.Tags.GetValueOrDefault("db.system") == "postgresql";

    // Npgsql names the statement db.statement (older semantic conventions) or db.query.text (current ones).
    private static string? Statement(CapturedSpan span) =>
        span.Tags.GetValueOrDefault("db.query.text") ?? span.Tags.GetValueOrDefault("db.statement");

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string subject, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Shared.CreateToken(subject));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed class PassThroughStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(static (context, nextMiddleware) => nextMiddleware(context));
            next(app);
        };
    }
}
