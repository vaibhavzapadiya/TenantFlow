using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TenantFlow.Smoke.Tests;

/// <summary>
/// End-to-end checks of the running local stack, at the milestone gate: real tokens from the local Keycloak are
/// accepted by the API, the real host enforces what an in-memory host cannot, and metrics, traces and logs from both
/// hosts reach Prometheus, Tempo and Loki and are kept for 7 days.
/// </summary>
public sealed partial class StackSmokeTests(LiveEnvironment stack)
{
    // SPEC-0002 criterion 10: a real token from the local provider is accepted by the API.
    [Fact]
    public async Task Keycloak_issues_a_token_that_the_api_accepts_on_me()
    {
        var token = await stack.TokenAsync("tenantflow", "tenantflow-dev-cli", "dev-user-a", Cancel);

        var me = await stack.JsonAsync($"{LiveEnvironment.Api}/v1/me", Cancel, token);

        Assert.NotNull(me);
        Assert.Equal($"{stack.Keycloak}/realms/tenantflow", me["issuer"]?.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(me["subject"]?.GetValue<string>()));
    }

    // SPEC-0002 criterion 9: an operator token reaches /platform (404 for an unknown tenant proves it was accepted) and
    // is refused on tenant routes. Needs the migrations, which "docker compose up" applies.
    [Fact]
    public async Task A_platform_operator_token_reaches_platform_and_not_me()
    {
        var token = await stack.TokenAsync("tenantflow-platform", "tenantflow-platform-dev-cli", "dev-operator", Cancel);

        Assert.Equal(HttpStatusCode.NotFound, await stack.StatusAsync($"{LiveEnvironment.Api}/v1/platform/tenants/{Guid.NewGuid()}", Cancel, token));
        Assert.Equal(HttpStatusCode.Unauthorized, await stack.StatusAsync($"{LiveEnvironment.Api}/v1/me", Cancel, token));
    }

    // SPEC-0014 criteria 5 and 6: the API reference's browser clients accept a token request from the API's own origin and
    // refuse any other; the script clients accept none from a browser. Keycloak answers every preflight, so the token
    // request itself is the check: a made-up code is refused with invalid_grant once the origin passed.
    [Theory]
    [InlineData("tenantflow", "tenantflow-api-docs")]
    [InlineData("tenantflow-platform", "tenantflow-platform-api-docs")]
    public async Task Keycloak_accepts_the_reference_sign_in_only_from_the_api_origin(string realm, string docsClient)
    {
        var devClient = realm == "tenantflow" ? "tenantflow-dev-cli" : "tenantflow-platform-dev-cli";

        using var fromApi = await TokenRequestAsync(realm, docsClient, LiveEnvironment.Api);
        using var fromElsewhere = await TokenRequestAsync(realm, docsClient, "http://localhost:5999");
        using var scriptClient = await TokenRequestAsync(realm, devClient, LiveEnvironment.Api);

        Assert.Equal(HttpStatusCode.BadRequest, fromApi.StatusCode);
        Assert.Contains("invalid_grant", await fromApi.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
        Assert.Equal([LiveEnvironment.Api], fromApi.Headers.GetValues("Access-Control-Allow-Origin"));
        Assert.Equal(HttpStatusCode.Forbidden, fromElsewhere.StatusCode);
        Assert.False(fromElsewhere.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(HttpStatusCode.Forbidden, scriptClient.StatusCode);
        Assert.False(scriptClient.Headers.Contains("Access-Control-Allow-Origin"));
    }

    // Pre-S5 review 2.4: the 64 KiB body limit is Kestrel's, so only a real host can prove it.
    [Fact]
    public async Task An_oversized_request_body_is_refused_with_413()
    {
        var token = await stack.TokenAsync("tenantflow-platform", "tenantflow-platform-dev-cli", "dev-operator", Cancel);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{LiveEnvironment.Api}/v1/platform/tenants"))
        {
            Content = new StringContent("{\"name\":\"" + new string('x', 70_000) + "\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = token;

        using var response = await stack.Http.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("\"code\":\"request.payload_too_large\"", await response.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prometheus_has_the_api_http_server_metrics() =>
        Assert.True(await stack.EventuallyAsync(token => HasPrometheusSeriesAsync("http_server_request_duration_seconds_count{service_name=\"tenantflow-api\"}", token), Cancel));

    [Fact]
    public async Task Prometheus_has_the_worker_heartbeat_counter() =>
        Assert.True(await stack.EventuallyAsync(token => HasPrometheusSeriesAsync("tenantflow_worker_heartbeats_total{service_name=\"tenantflow-worker\"}", token), Cancel));

    [Fact]
    public async Task Tempo_has_an_api_trace() =>
        Assert.True(await stack.EventuallyAsync(
            async token => (await stack.JsonAsync($"{stack.Tempo}/api/search?tags={Uri.EscapeDataString("service.name=tenantflow-api")}&limit=5", token))?["traces"]?.AsArray().Count > 0,
            Cancel));

    [Theory]
    [InlineData("tenantflow-api")]
    [InlineData("tenantflow-worker")]
    public async Task Loki_has_logs_with_trace_ids(string service) =>
        Assert.True(await stack.EventuallyAsync(
            async token =>
            {
                var query = Uri.EscapeDataString($"{{service_name=\"{service}\"}} | trace_id != \"\"");
                var since = (stack.Started - TimeSpan.FromMinutes(15)).ToUnixTimeMilliseconds() * 1_000_000;
                var result = await stack.JsonAsync($"{stack.Loki}/loki/api/v1/query_range?query={query}&start={since}&limit=5", token);
                return result?["data"]?["result"]?.AsArray().Count > 0;
            },
            Cancel));

    // SPEC-0010 criterion 8 (ADR-0025): each backend reports 7 days of retention from its running configuration.
    [Fact]
    public async Task Prometheus_loki_and_tempo_keep_7_days_of_telemetry()
    {
        var flags = await stack.JsonAsync($"{stack.Prometheus}/api/v1/status/flags", Cancel);
        var loki = await stack.Http.GetStringAsync(new Uri($"{stack.Loki}/config"), Cancel);
        var tempo = await stack.Http.GetStringAsync(new Uri($"{stack.Tempo}/status/config"), Cancel);

        // Prometheus and Loki print 7 days as "1w", Tempo as "168h0m0s".
        Assert.Contains(flags?["data"]?["storage.tsdb.retention.time"]?.GetValue<string>(), SevenDays);
        Assert.Matches(LokiRetentionEnabled(), loki);
        Assert.Matches(LokiRetentionPeriod(), loki);
        Assert.Equal(2, TempoBlockRetention().Count(tempo));
    }

    // SPEC-0010 criterion 3: Prometheus scrapes the broker's queue metrics.
    [Fact]
    public async Task Prometheus_scrapes_the_rabbitmq_queue_metrics() =>
        Assert.True(await stack.EventuallyAsync(
            async token => (await PrometheusResultAsync("up{job=\"rabbitmq\"}", token))?.Count(static series => series?["value"]?[1]?.GetValue<string>() == "1") == 1,
            Cancel));

    // SPEC-0010 S3 (ADR-0029): the dashboards in the repository are provisioned.
    [Fact]
    public async Task Grafana_has_the_golden_signal_worker_and_business_dashboards()
    {
        var dashboards = await stack.JsonAsync($"{stack.Grafana}/api/search?tag=tenantflow", Cancel, stack.GrafanaAdmin);
        var uids = dashboards?.AsArray().Select(static dashboard => dashboard?["uid"]?.GetValue<string>()).ToList() ?? [];

        Assert.Subset(uids.ToHashSet(), new HashSet<string?> { "tenantflow-golden-signals", "tenantflow-worker", "tenantflow-business" });
    }

    // SPEC-0010 criterion 13: on a healthy stack under smoke traffic no alert fires (pending ones may still clear).
    [Fact]
    public async Task Prometheus_loads_the_rules_and_no_alert_is_firing() =>
        Assert.True(await stack.EventuallyAsync(
            async token =>
            {
                var groups = (await stack.JsonAsync($"{stack.Prometheus}/api/v1/rules", token))?["data"]?["groups"]?.AsArray().Count ?? 0;
                var alerts = (await stack.JsonAsync($"{stack.Prometheus}/api/v1/alerts", token))?["data"]?["alerts"]?.AsArray();
                var firing = alerts?.Count(static alert => alert?["state"]?.GetValue<string>() == "firing") ?? int.MaxValue;
                return groups >= 9 && firing == 0 && await stack.StatusAsync($"{stack.Alertmanager}/-/ready", token) == HttpStatusCode.OK;
            },
            Cancel));

    // SPEC-0001: Grafana is never anonymous.
    [Fact]
    public async Task Grafana_refuses_anonymous_access() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await stack.StatusAsync($"{stack.Grafana}/api/datasources", Cancel));

    [Fact]
    public async Task Grafana_has_prometheus_loki_and_tempo_with_trace_to_logs_links()
    {
        var sources = (await stack.JsonAsync($"{stack.Grafana}/api/datasources", Cancel, stack.GrafanaAdmin))?.AsArray() ?? [];
        var types = sources.Select(static source => source?["type"]?.GetValue<string>()).ToList();
        var tempo = sources.Single(static source => source?["type"]?.GetValue<string>() == "tempo");

        Assert.Equal(3, types.Count(static type => type is "prometheus" or "loki" or "tempo"));
        Assert.Equal("loki", tempo?["jsonData"]?["tracesToLogsV2"]?["datasourceUid"]?.GetValue<string>());
    }

    private static readonly string[] SevenDays = ["1w", "7d"];

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private async Task<HttpResponseMessage> TokenRequestAsync(string realm, string clientId, string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{stack.Keycloak}/realms/{realm}/protocol/openid-connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["code"] = "not-a-code",
                ["redirect_uri"] = $"{LiveEnvironment.Api}/scalar/v1",
                ["code_verifier"] = new string('v', 43),
            }),
        };
        request.Headers.Add("Origin", origin);
        return await stack.Http.SendAsync(request, Cancel);
    }

    private async Task<bool> HasPrometheusSeriesAsync(string query, CancellationToken cancellationToken) =>
        (await PrometheusResultAsync(query, cancellationToken))?.Count > 0;

    private async Task<JsonArray?> PrometheusResultAsync(string query, CancellationToken cancellationToken) =>
        (await stack.JsonAsync($"{stack.Prometheus}/api/v1/query?query={Uri.EscapeDataString(query)}", cancellationToken))?["data"]?["result"]?.AsArray();

    [GeneratedRegex(@"(?m)^\s+retention_enabled: true$")]
    private static partial Regex LokiRetentionEnabled();

    [GeneratedRegex(@"(?ms)^limits_config:.*?^\s+retention_period: 1w$")]
    private static partial Regex LokiRetentionPeriod();

    [GeneratedRegex(@"(?m)^\s+block_retention: 168h0m0s$")]
    private static partial Regex TempoBlockRetention();
}
