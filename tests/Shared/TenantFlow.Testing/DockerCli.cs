using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace TenantFlow.Testing;

/// <summary>The exit code and output of one command.</summary>
public sealed record CommandResult(int ExitCode, string Output, string Error);

/// <summary>
/// The Docker CLI for tests that drive containers (SPEC-0013 criterion 10, ADR-0037): the host <c>docker</c> when its
/// engine answers, otherwise, on Windows, Docker inside WSL through <c>wsl.exe -e docker</c>. This is the one place that
/// knows about that choice. Commands run from the repository root, so relative paths work in both.
/// </summary>
public static class DockerCli
{
    private static readonly Lazy<Task<bool>> UseWslTask = new(DetectAsync);

    public static string RepositoryRoot { get; } = FindRoot();

    /// <summary>True when Docker runs inside WSL, so host paths are translated for volume mounts.</summary>
    public static Task<bool> UsesWslAsync() => UseWslTask.Value;

    /// <summary>
    /// Runs <c>docker</c> with the arguments, from the repository root. Through <c>wsl.exe</c> about one call in ten fails
    /// within a fraction of a second, with no output, while the engine is up (measured 2026-10-23); every command the tests
    /// run is safe to repeat, so a call that fails that fast is tried up to three times.
    /// </summary>
    public static async Task<CommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var wsl = await UsesWslAsync();
        CommandResult result = new(1, string.Empty, string.Empty);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var started = Stopwatch.StartNew();
            result = await StartAsync(wsl ? "wsl.exe" : "docker", wsl ? ["-e", "docker", .. arguments] : arguments, cancellationToken);
            if (result.ExitCode == 0 || started.Elapsed >= TimeSpan.FromSeconds(1))
            {
                break;
            }
        }

        return result;
    }

    /// <summary>A host path as the Docker engine sees it, for a volume mount.</summary>
    public static async Task<string> EnginePathAsync(string path, CancellationToken cancellationToken)
    {
        if (!await UsesWslAsync())
        {
            return path;
        }

        var translated = await StartAsync("wsl.exe", ["-e", "wslpath", "-a", path.Replace('\\', '/')], cancellationToken);
        return translated.ExitCode == 0
            ? translated.Output.Trim()
            : throw new InvalidOperationException($"wslpath could not translate {path}.");
    }

    /// <summary>Whether a file exists on the Docker host (for the host's CA bundle).</summary>
    public static async Task<bool> EngineFileExistsAsync(string path, CancellationToken cancellationToken) =>
        await UsesWslAsync()
            ? (await StartAsync("wsl.exe", ["-e", "test", "-f", path], cancellationToken)).ExitCode == 0
            : File.Exists(path);

    /// <summary>Runs any program from the repository root and collects its output (git, dotnet).</summary>
    public static async Task<CommandResult> StartAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"{fileName} did not start.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new CommandResult(process.ExitCode, await output, await error);
        }
        catch (Win32Exception)
        {
            // The program is not installed.
            return new CommandResult(127, string.Empty, $"{fileName} was not found.");
        }
    }

    private static async Task<bool> DetectAsync()
    {
        var info = new[] { "info", "--format", "{{.ServerVersion}}" };
        if ((await StartAsync("docker", info, CancellationToken.None)).ExitCode == 0)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                if ((await StartAsync("wsl.exe", ["-e", "docker", .. info], CancellationToken.None)).ExitCode == 0)
                {
                    return true;
                }
            }
        }

        throw new InvalidOperationException(
            "The Docker engine is not reachable. Start Docker Desktop, or Docker inside WSL; see docs/operations/local-environment.md.");
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TenantFlow.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
