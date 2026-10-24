using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using TenantFlow.BuildingBlocks.Observability;
using TenantFlow.Worker.Health;

namespace TenantFlow.Worker.Tests;

/// <summary>The deployed Worker's container health check: 0 only when its own liveness endpoint answers on loopback.</summary>
public sealed class LivenessProbeTests
{
    [Fact]
    public async Task The_probe_passes_when_the_liveness_endpoint_answers_and_fails_otherwise()
    {
        var ct = TestContext.Current.CancellationToken;
        var port = FreePort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        await using var host = builder.Build();
        host.MapGet(HealthEndpoints.LivePath, static () => Results.Text("Healthy"));
        await host.StartAsync(ct);

        var live = await LivenessProbe.RunAsync($"{port}", ct);
        await host.StopAsync(ct);
        var down = await LivenessProbe.RunAsync($"{port}", ct);

        Assert.Equal((0, 1), (live, down));
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
