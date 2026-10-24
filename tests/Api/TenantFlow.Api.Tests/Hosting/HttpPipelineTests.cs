using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection;

namespace TenantFlow.Api.Tests.Hosting;

/// <summary>
/// The HTTP pipeline as a whole (pre-S5 review 2.8; ADR-0017): every response, including errors, carries the
/// baseline security headers, and a fault in routing itself is still answered by the exception handler.
/// </summary>
public sealed class HttpPipelineTests
{
    private static readonly (string Header, string Value)[] BaselineHeaders =
    [
        ("X-Content-Type-Options", "nosniff"),
        ("Cache-Control", "no-store"),
        ("Referrer-Policy", "no-referrer"),
        ("Content-Security-Policy", "default-src 'none'; frame-ancestors 'none'"),
    ];

    [Theory]
    [InlineData("/health/live", HttpStatusCode.OK)]
    [InlineData("/v1/me", HttpStatusCode.Unauthorized)]
    [InlineData("/no/such/path", HttpStatusCode.Unauthorized)]
    public async Task Every_response_carries_the_baseline_security_headers(string path, HttpStatusCode expected)
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
        AssertBaselineHeaders(response);
    }

    [Fact]
    public async Task A_405_carries_the_baseline_security_headers()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = Client(factory);

        using var response = await client.GetAsync(new Uri("/v1/platform/tenants", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        AssertBaselineHeaders(response);
    }

    [Fact]
    public async Task An_unexpected_fault_still_carries_the_baseline_security_headers()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = Client(factory);

        // PostgreSQL is unreachable in this host, so the use case fails for real and the exception handler answers.
        using var response = await client.PostAsync(
            new Uri("/v1/platform/tenants", UriKind.Relative), new StringContent("""{"name":"Acme"}""", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertBaselineHeaders(response);
    }

    [Fact]
    public async Task A_fault_inside_routing_is_answered_by_the_exception_handler()
    {
        // Routing is a step of the pipeline like any other. If the host leaves it to be inserted implicitly, it runs
        // before the exception handler and a fault in it escapes as a raw exception instead of a Problem Details 500.
        await using var factory = ApiFactory.CreateProduction(services => services.AddSingleton<EndpointSelector, ThrowingEndpointSelector>());
        using var client = factory.CreateClient();

        HttpResponseMessage? response = null;
        var escaped = await Record.ExceptionAsync(async () =>
            response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken));

        Assert.Null(escaped);
        Assert.NotNull(response);
        using var owned = response;
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("server.unexpected", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("selector-fault", body, StringComparison.Ordinal);
    }

    private static void AssertBaselineHeaders(HttpResponseMessage response)
    {
        foreach (var (header, value) in BaselineHeaders)
        {
            Assert.True(response.Headers.TryGetValues(header, out var values), $"{header} is missing on a {(int)response.StatusCode}");
            Assert.Equal(value, Assert.Single(values));
        }
    }

    private static HttpClient Client(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Platform.CreateOperatorToken());
        return client;
    }

    private sealed class ThrowingEndpointSelector : EndpointSelector
    {
        public override Task SelectAsync(HttpContext httpContext, CandidateSet candidates) =>
            throw new InvalidOperationException("selector-fault");
    }
}
