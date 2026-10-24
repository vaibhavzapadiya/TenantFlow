using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

[assembly: AssemblyFixture(typeof(TenantFlow.Smoke.Tests.LiveEnvironment))]

namespace TenantFlow.Smoke.Tests;

/// <summary>
/// The running local stack: both hosts, Keycloak, and the observability profile, at the addresses Compose and the
/// launch profiles use (<see cref="ComposeSettings"/>). Starting it sends the traffic that the telemetry checks then
/// look for; every check that waits for telemetry polls until <see cref="Deadline"/>.
/// </summary>
public sealed class LiveEnvironment : IAsyncLifetime
{
    public const string Api = "http://localhost:5080";
    public const string Worker = "http://localhost:5081";

    // Telemetry is exported in batches (metrics every 10 seconds in Development).
    private static readonly TimeSpan TelemetryWait = TimeSpan.FromSeconds(90);

    public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(10) };

    public string Keycloak { get; } = $"http://localhost:{ComposeSettings.Get("KEYCLOAK_PORT")}";

    public string Prometheus { get; } = $"http://127.0.0.1:{ComposeSettings.Get("PROMETHEUS_PORT")}";

    public string Loki { get; } = $"http://127.0.0.1:{ComposeSettings.Get("LOKI_PORT")}";

    public string Tempo { get; } = $"http://127.0.0.1:{ComposeSettings.Get("TEMPO_PORT")}";

    public string Grafana { get; } = $"http://127.0.0.1:{ComposeSettings.Get("GRAFANA_PORT")}";

    public string Alertmanager { get; } = $"http://127.0.0.1:{ComposeSettings.Get("ALERTMANAGER_PORT")}";

    public DateTimeOffset Started { get; private set; }

    public DateTimeOffset Deadline => Started + TelemetryWait;

    public AuthenticationHeaderValue GrafanaAdmin { get; } = new(
        "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{ComposeSettings.Get("GRAFANA_ADMIN_PASSWORD")}")));

    public async ValueTask InitializeAsync()
    {
        Started = DateTimeOffset.UtcNow;
        var cancellationToken = TestContext.Current.CancellationToken;
        if (await StatusAsync($"{Worker}/health/live", cancellationToken) != HttpStatusCode.OK)
        {
            throw new InvalidOperationException("The Worker is not running. Start it with: dotnet run --project src/Hosts/TenantFlow.Worker");
        }

        // Any non-health request is traced; its 401 (deny by default, SPEC-0002) is fine. Health probes are not traced.
        if (await StatusAsync($"{Api}/smoke-probe", cancellationToken) is null)
        {
            throw new InvalidOperationException("The API is not running. Start it with: dotnet run --project src/Hosts/TenantFlow.Api");
        }
    }

    public ValueTask DisposeAsync()
    {
        Http.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>The status of a GET, or null when nothing answered.</summary>
    public async Task<HttpStatusCode?> StatusAsync(string uri, CancellationToken cancellationToken, AuthenticationHeaderValue? authorization = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri));
        request.Headers.Authorization = authorization;
        try
        {
            using var response = await Http.SendAsync(request, cancellationToken);
            return response.StatusCode;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>A GET's JSON body, or null when the request failed.</summary>
    public async Task<JsonNode?> JsonAsync(string uri, CancellationToken cancellationToken, AuthenticationHeaderValue? authorization = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri));
        request.Headers.Authorization = authorization;
        try
        {
            using var response = await Http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode ? JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>A bearer token of a synthetic user from the local Keycloak (development clients allow the password grant).</summary>
    public async Task<AuthenticationHeaderValue> TokenAsync(string realm, string client, string user, CancellationToken cancellationToken)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = client,
            ["username"] = user,
            ["password"] = ComposeSettings.Get("KEYCLOAK_DEV_USER_PASSWORD"),
        });
        using var response = await Http.PostAsync(new Uri($"{Keycloak}/realms/{realm}/protocol/openid-connect/token"), form, cancellationToken);
        response.EnsureSuccessStatusCode();
        var token = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))?["access_token"]?.GetValue<string>();
        return new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>Polls a check until it holds or the telemetry deadline passes.</summary>
    public async Task<bool> EventuallyAsync(Func<CancellationToken, Task<bool>> check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(check);
        while (true)
        {
            if (await check(cancellationToken))
            {
                return true;
            }

            if (DateTimeOffset.UtcNow >= Deadline)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
    }
}
