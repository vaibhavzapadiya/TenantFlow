using TenantFlow.BuildingBlocks.Observability;

namespace TenantFlow.Worker.Health;

/// <summary>
/// The container health check of the deployed Worker (deploy/terraform/environment/services.tf). The chiseled image has
/// no shell or curl, so ECS runs <c>dotnet TenantFlow.Worker.dll --probe-live</c>: it asks the running host's own
/// <c>/health/live</c> on loopback and exits 0 or 1, without building a second host. Liveness only: a broker or database
/// outage must not make ECS replace a Worker that would recover by itself.
/// </summary>
internal static class LivenessProbe
{
    public const string Argument = "--probe-live";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>0 when the host on the first of <paramref name="httpPorts"/> (as in ASPNETCORE_HTTP_PORTS) answers live.</summary>
    public static async Task<int> RunAsync(string? httpPorts, CancellationToken cancellationToken)
    {
        var port = (httpPorts ?? "8080").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "8080";
        using var client = new HttpClient { Timeout = Timeout };
        try
        {
            using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}{HealthEndpoints.LivePath}"), cancellationToken);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
