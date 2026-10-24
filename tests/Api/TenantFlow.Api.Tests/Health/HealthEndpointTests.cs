using System.Net;

namespace TenantFlow.Api.Tests.Health;

/// <summary>
/// SPEC-0001 criteria 6 and 8: liveness never depends on PostgreSQL, readiness does, and health
/// responses reveal nothing but the status word.
/// </summary>
public sealed class HealthEndpointTests
{
    [Fact]
    public async Task Live_returns_200_with_only_the_status_word_while_PostgreSQL_is_unreachable()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", body);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Ready_returns_503_with_only_the_status_word_while_PostgreSQL_is_unreachable()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        // Exactly the status word: no exception text, host, port, or dependency name.
        Assert.Equal("Unhealthy", body);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
    }
}
