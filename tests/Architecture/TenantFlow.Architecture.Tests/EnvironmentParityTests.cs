using System.Text.RegularExpressions;

namespace TenantFlow.Architecture.Tests;

/// <summary>
/// SPEC-0012 criterion 3 (ADR-0034 decision 2): dev, staging and prod differ only in values, never in which settings
/// they declare, so no environment silently runs on a default the others override.
/// </summary>
public sealed partial class EnvironmentParityTests
{
    private static readonly string Root = FindRoot();
    private static readonly string[] Environments = ["dev", "staging", "prod"];

    [Fact]
    public void Every_environment_declares_the_same_settings()
    {
        var keys = Environments.ToDictionary(static name => name, static name => TopLevelKeys(name));

        Assert.All(Environments, name => Assert.Equal(keys["staging"].Order(StringComparer.Ordinal), keys[name].Order(StringComparer.Ordinal)));
    }

    // The names assigned at the top level of envs/<name>.tfvars (object members are indented, so they are not counted).
    private static HashSet<string> TopLevelKeys(string environment)
    {
        var text = File.ReadAllText(Path.Combine(Root, "deploy", "terraform", "environment", "envs", $"{environment}.tfvars"));
        return [.. TopLevelAssignment().Matches(text).Select(static m => m.Groups["key"].Value)];
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

    [GeneratedRegex(@"^(?<key>[a-z_]+)\s*=", RegexOptions.Multiline)]
    private static partial Regex TopLevelAssignment();
}
