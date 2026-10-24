using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace TenantFlow.Migrator.Provisioning;

/// <summary>
/// Creates or re-asserts the application's RabbitMQ user through the management API, as the operator account (MW-A02):
/// no tags, so it can neither use the management API nor administer the broker, and full rights only inside its own
/// virtual host. Then checks that the management API refuses that user. Idempotent.
/// </summary>
internal static class BrokerProvisioner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public static async Task ProvisionAsync(BrokerProvisioningOptions options, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = new Uri(options.ManagementUrl!.TrimEnd('/') + "/"), Timeout = Timeout };
        client.DefaultRequestHeaders.Authorization = Basic(options.OperatorUsername!, options.OperatorPassword!);
        var vhost = Uri.EscapeDataString(options.VirtualHost!);
        var user = Uri.EscapeDataString(options.AppUsername!);

        await PutAsync(client, $"api/vhosts/{vhost}", new { }, cancellationToken);
        await PutAsync(client, $"api/users/{user}", new { password = options.AppPassword, tags = string.Empty }, cancellationToken);
        await PutAsync(client, $"api/permissions/{vhost}/{user}", new { configure = ".*", write = ".*", read = ".*" }, cancellationToken);

        using var whoami = new HttpRequestMessage(HttpMethod.Get, "api/whoami");
        whoami.Headers.Authorization = Basic(options.AppUsername!, options.AppPassword!);
        using var response = await client.SendAsync(whoami, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException($"The application user reached the management API (HTTP {(int)response.StatusCode}); it must be refused.");
        }
    }

    private static async Task PutAsync(HttpClient client, string path, object body, CancellationToken cancellationToken)
    {
        using var response = await client.PutAsJsonAsync(path, body, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // The status and the path only: the request body holds a password.
            throw new InvalidOperationException($"The management API answered {(int)response.StatusCode} to PUT /{path}.");
        }
    }

    private static AuthenticationHeaderValue Basic(string username, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
}
