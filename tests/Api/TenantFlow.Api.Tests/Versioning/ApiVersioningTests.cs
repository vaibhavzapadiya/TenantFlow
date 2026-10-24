using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace TenantFlow.Api.Tests.Versioning;

/// <summary>
/// URL-segment versioning (pre-S5 review 2.1; blueprint section 9). Every business endpoint lives
/// under <c>/v1</c>; only the health probes are unversioned, because platforms call them without
/// knowing the API's version. Paths here are literals on purpose: they are the public contract.
/// </summary>
public sealed class ApiVersioningTests
{
    private static readonly string[] UnversionedHealthPaths = ["/health/live", "/health/ready"];

    [Fact]
    public async Task Every_endpoint_except_the_health_probes_lives_under_a_version_segment()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();

        var patterns = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(static endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToList();

        Assert.NotEmpty(patterns);
        var unversioned = patterns.Where(static pattern =>
            !pattern.StartsWith("/v1/", StringComparison.Ordinal) && !UnversionedHealthPaths.Contains(pattern, StringComparer.Ordinal));
        Assert.Empty(unversioned);
    }

    [Theory]
    [InlineData("/me")]
    [InlineData("/platform/tenants")]
    [InlineData("/v2/me")]
    [InlineData("/v2/platform/tenants")]
    public async Task Unversioned_or_unknown_versions_are_not_served_to_signed_in_callers(string path)
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Shared.CreateToken());

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // Deny-by-default (SPEC-0002 criterion 8): a path that is not a versioned endpoint reveals nothing, not even whether the token was good.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Version_one_serves_the_same_identity_endpoint()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/v1/me", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Shared.CreateToken());

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
