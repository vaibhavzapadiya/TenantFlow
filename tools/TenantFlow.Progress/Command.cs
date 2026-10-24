using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace TenantFlow.Progress;

/// <summary>The exit code and standard output lines of one command.</summary>
internal sealed record CommandResult(int ExitCode, IReadOnlyList<string> Lines)
{
    public string? FirstLine => Lines.Count > 0 ? Lines[0] : null;
}

/// <summary>Runs git and dotnet from the repository root. A missing program yields exit code 127 and no output.</summary>
internal static class Command
{
    public static async Task<CommandResult> RunAsync(string root, string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
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
            _ = await error;
            var text = await output;
            return new CommandResult(process.ExitCode, text.Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => line.Length > 0).ToList());
        }
        catch (Win32Exception)
        {
            return new CommandResult(127, []);
        }
    }
}
