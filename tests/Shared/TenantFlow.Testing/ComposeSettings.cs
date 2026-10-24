using System.Text.RegularExpressions;

namespace TenantFlow.Testing;

/// <summary>
/// The local Compose stack's settings as Compose itself resolves them (ADR-0037): environment variables first, then the
/// optional, git-ignored <c>deploy/compose/.env</c>, otherwise the default in <c>deploy/compose/compose.yaml</c>. Tests
/// that talk to the running stack read their settings here, so no script passes them in and no value is copied.
/// Values are never printed.
/// </summary>
public static partial class ComposeSettings
{
    private static readonly Lazy<string> ComposeDirectory = new(FindComposeDirectory);

    public static string Get(string name)
    {
        if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } fromEnvironment)
        {
            return fromEnvironment;
        }

        var envFile = Path.Combine(ComposeDirectory.Value, ".env");
        if (File.Exists(envFile))
        {
            foreach (var line in File.ReadLines(envFile))
            {
                var assignment = Assignment().Match(line);
                if (assignment.Success && assignment.Groups["name"].Value == name)
                {
                    // The first assignment wins, with one pair of surrounding quotes stripped, as Compose does.
                    return Unquote(assignment.Groups["value"].Value);
                }
            }
        }

        var compose = File.ReadAllText(Path.Combine(ComposeDirectory.Value, "compose.yaml"));
        return Default().Matches(compose).FirstOrDefault(match => match.Groups["name"].Value == name)?.Groups["value"].Value
            ?? throw new InvalidOperationException($"deploy/compose/compose.yaml has no default for {name}.");
    }

    public static int GetPort(string name) => int.Parse(Get(name), System.Globalization.CultureInfo.InvariantCulture);

    private static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0] ? value[1..^1] : value;

    private static string FindComposeDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TenantFlow.sln")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new InvalidOperationException("The repository root was not found.")
            : Path.Combine(directory.FullName, "deploy", "compose");
    }

    [GeneratedRegex(@"^\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>.*?)\s*$")]
    private static partial Regex Assignment();

    [GeneratedRegex(@"\$\{(?<name>[A-Z_][A-Z0-9_]*):-(?<value>[^}]*)\}")]
    private static partial Regex Default();
}
