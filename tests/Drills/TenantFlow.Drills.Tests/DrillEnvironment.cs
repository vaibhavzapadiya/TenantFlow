using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace TenantFlow.Drills.Tests;

/// <summary>
/// The running local stack as a drill sees it: the API, Keycloak, RabbitMQ's management API and Alertmanager, at the
/// addresses Compose and the launch profiles use (<see cref="ComposeSettings"/>), and Compose itself through
/// <see cref="DockerCli"/>. Every step is written to the test output with its time, for docs/operations/drills.
/// </summary>
public sealed class DrillEnvironment : IAsyncLifetime
{
    public const string Api = "http://localhost:5080";
    public const string Worker = "http://localhost:5081";

    private readonly string _alertmanager = $"http://127.0.0.1:{ComposeSettings.Get("ALERTMANAGER_PORT")}";
    private readonly string _keycloak = $"http://localhost:{ComposeSettings.Get("KEYCLOAK_PORT")}";

    public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async ValueTask InitializeAsync()
    {
        foreach (var host in new[] { Api, Worker })
        {
            try
            {
                using var response = await Http.GetAsync(new Uri($"{host}/health/live"), TestContext.Current.CancellationToken);
                response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException)
            {
                throw new InvalidOperationException("Both hosts must be running (API on :5080, Worker on :5081) before a drill.");
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Http.Dispose();
        return ValueTask.CompletedTask;
    }

    public static void Log(string what, DateTimeOffset since)
    {
        var now = DateTimeOffset.UtcNow;
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {what,-34} {now:HH:mm:ss} UTC (+{(now - since).TotalSeconds:N0} s)"));
    }

    public static void Note(string message) => TestContext.Current.TestOutputHelper?.WriteLine($"  {message}");

    public static async Task ComposeAsync(string command, string service, CancellationToken cancellationToken)
    {
        var result = await DockerCli.RunAsync(["compose", "-f", "deploy/compose/compose.yaml", command, service], cancellationToken);
        Assert.True(result.ExitCode == 0, $"docker compose {command} {service} failed: {result.Error}");
    }

    /// <summary>The active Alertmanager alert of that name, or null.</summary>
    public async Task<JsonNode?> ActiveAlertAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var alerts = JsonNode.Parse(await Http.GetStringAsync(new Uri($"{_alertmanager}/api/v2/alerts?active=true"), cancellationToken))?.AsArray() ?? [];
            return alerts.FirstOrDefault(alert => alert?["labels"]?["alertname"]?.GetValue<string>() == name);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Waits until the alert is active (or, with <paramref name="resolved"/>, until it is not); null on timeout.</summary>
    public async Task<JsonNode?> WaitForAlertAsync(string name, TimeSpan within, bool resolved, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + within;
        while (true)
        {
            var alert = await ActiveAlertAsync(name, cancellationToken);
            if (resolved ? alert is null : alert is not null)
            {
                return alert ?? new JsonObject();
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        }
    }

    public async Task<AuthenticationHeaderValue> TokenAsync(string realm, string client, string user, CancellationToken cancellationToken)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = client,
            ["username"] = user,
            ["password"] = ComposeSettings.Get("KEYCLOAK_DEV_USER_PASSWORD"),
        });
        using var response = await Http.PostAsync(new Uri($"{_keycloak}/realms/{realm}/protocol/openid-connect/token"), form, cancellationToken);
        response.EnsureSuccessStatusCode();
        return new("Bearer", JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))?["access_token"]?.GetValue<string>());
    }

    public async Task<JsonNode?> ApiAsync(HttpMethod method, string path, AuthenticationHeaderValue token, object? body, CancellationToken cancellationToken, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, new Uri($"{Api}{path}")) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = token;
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        using var response = await Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    /// <summary>Publishes to the default exchange through RabbitMQ's management API.</summary>
    public async Task PublishAsync(string routingKey, string messageId, string type, string payload, CancellationToken cancellationToken)
    {
        var management = $"http://127.0.0.1:{ComposeSettings.Get("RABBITMQ_MANAGEMENT_PORT")}";
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{management}/api/exchanges/%2F/amq.default/publish"))
        {
            Content = JsonContent.Create(new
            {
                properties = new { message_id = messageId, type },
                routing_key = routingKey,
                payload,
                payload_encoding = "string",
            }),
        };
        request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ComposeSettings.Get("RABBITMQ_USER")}:{ComposeSettings.Get("RABBITMQ_PASSWORD")}")));
        using var response = await Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
