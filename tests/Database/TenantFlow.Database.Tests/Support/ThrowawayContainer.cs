namespace TenantFlow.Database.Tests.Support;

/// <summary>
/// A container a test starts for itself through the Docker CLI, on a loopback port of its own, and removes afterwards:
/// for checks that would change the shared Compose stack (cluster-wide roles, broker users). Never part of Compose.
/// </summary>
public sealed class ThrowawayContainer : IAsyncDisposable
{
    private readonly string _id;

    private ThrowawayContainer(string id, string address)
    {
        _id = id;
        Address = address;
    }

    /// <summary>The published port's address, for example 127.0.0.1:49153.</summary>
    public string Address { get; }

    public string Host => Address[..Address.LastIndexOf(':')];

    public int Port => int.Parse(Address[(Address.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

    public static async Task<ThrowawayContainer> StartAsync(string image, int containerPort, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
    {
        List<string> arguments = ["run", "--detach", "--rm", "--publish", $"127.0.0.1::{containerPort}"];
        foreach (var (name, value) in environment)
        {
            arguments.AddRange(["--env", $"{name}={value}"]);
        }

        arguments.Add(image);
        var run = await DockerCli.RunAsync(arguments, cancellationToken);
        Assert.True(run.ExitCode == 0, $"{image} did not start (exit {run.ExitCode}); is the Docker engine running? {run.Error}");
        var id = run.Output.Trim();

        var port = await DockerCli.RunAsync(["port", id, $"{containerPort}/tcp"], cancellationToken);
        Assert.True(port.ExitCode == 0, $"The port of {image} was not published (exit {port.ExitCode}): {port.Error}");
        return new ThrowawayContainer(id, port.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0]);
    }

    /// <summary>Calls <paramref name="probe"/> until it stops throwing, for at most a minute.</summary>
    public static async Task WaitUntilAsync(Func<Task> probe, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1);
        while (true)
        {
            try
            {
                await probe();
                return;
            }
            catch (Exception) when (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync() => await DockerCli.RunAsync(["rm", "--force", _id], CancellationToken.None);
}
