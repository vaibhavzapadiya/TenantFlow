using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TenantFlow.Api.Tests.Telemetry;

/// <summary>
/// SPEC-0002 criterion 23: every authentication and authorization denial is counted in
/// <c>tenantflow.auth.denials</c> with only <c>scheme</c> and <c>reason</c> labels, and logged without the token or the
/// <c>Authorization</c> header. The denials that need a database (membership, suspension, permission) are proven in
/// the database tests; these cover the ones decided by the token alone.
/// </summary>
public sealed class AuthDenialTelemetryTests
{
    private const string Counter = "tenantflow.auth.denials";

    [Fact]
    public async Task A_missing_token_is_counted_once_with_the_scheme_and_reason_only()
    {
        using var meter = new MeterCapture(Counter);
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();
        meter.Clear();

        using var response = await client.GetAsync("/v1/me", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertSingle(meter, "tenant", "no_token");
    }

    [Fact]
    public async Task A_token_that_is_refused_is_counted_as_invalid_on_the_scheme_that_refused_it()
    {
        using var meter = new MeterCapture(Counter);
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();
        meter.Clear();

        // A platform token on a tenant route and a tenant token on a platform route: each scheme refuses the other's.
        using var onTenantRoute = await GetAsync(client, "/v1/me", TestIssuer.Platform.CreateOperatorToken());
        using var onPlatformRoute = await GetAsync(client, $"/v1/platform/tenants/{Guid.NewGuid()}", TestIssuer.Shared.CreateToken("user-a"));

        Assert.Equal(HttpStatusCode.Unauthorized, onTenantRoute.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, onPlatformRoute.StatusCode);
        Assert.Equal(
            [("tenant", "invalid_token"), ("platform", "invalid_token")],
            meter.Measurements.Select(static m => (m.Tags["scheme"]!, m.Tags["reason"]!)).ToList());
    }

    [Fact]
    public async Task A_platform_caller_without_the_operator_role_is_counted_as_not_operator()
    {
        using var meter = new MeterCapture(Counter);
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();
        meter.Clear();

        using var response = await GetAsync(client, $"/v1/platform/tenants/{Guid.NewGuid()}", TestIssuer.Platform.CreateToken("not-an-operator"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertSingle(meter, "platform", "not_operator");
    }

    [Fact]
    public async Task A_route_with_no_policy_is_counted_as_a_denial_too()
    {
        using var meter = new MeterCapture(Counter);
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();
        meter.Clear();

        using var response = await client.GetAsync("/no-such-route", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertSingle(meter, "tenant", "no_token");
    }

    [Fact]
    public async Task Allowed_requests_and_health_probes_are_not_counted()
    {
        using var meter = new MeterCapture(Counter);
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();
        meter.Clear();

        using var allowed = await GetAsync(client, "/v1/me", TestIssuer.Shared.CreateToken("user-a"));
        using var live = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Empty(meter.Measurements);
    }

    [Fact]
    public async Task Logs_describe_the_denial_but_never_carry_the_token_or_the_authorization_header()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = ApiFactory.CreateProduction(services => services.AddSingleton<ILoggerProvider>(logs));
        using var client = factory.CreateClient();
        var wrongIssuerToken = TestIssuer.Platform.CreateToken("subject-that-must-not-be-logged");
        var signature = wrongIssuerToken[(wrongIssuerToken.LastIndexOf('.') + 1)..];
        var payload = wrongIssuerToken.Split('.')[1];

        using var refused = await GetAsync(client, "/v1/me", wrongIssuerToken);
        using var anonymous = await client.GetAsync("/v1/me", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var all = string.Join(Environment.NewLine, logs.Messages);
        Assert.Contains("Access denied: scheme tenant, reason invalid_token", all, StringComparison.Ordinal);
        Assert.Contains("Access denied: scheme tenant, reason no_token", all, StringComparison.Ordinal);
        Assert.DoesNotContain(wrongIssuerToken, all, StringComparison.Ordinal);
        Assert.DoesNotContain(signature, all, StringComparison.Ordinal);
        Assert.DoesNotContain(payload, all, StringComparison.Ordinal);
        Assert.DoesNotContain("subject-that-must-not-be-logged", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer ey", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", all, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertSingle(MeterCapture meter, string scheme, string reason)
    {
        var measurement = Assert.Single(meter.Measurements);
        Assert.Equal(1, measurement.Value);
        Assert.Equal(["reason", "scheme"], measurement.Tags.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(scheme, measurement.Tags["scheme"]);
        Assert.Equal(reason, measurement.Tags["reason"]);
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
