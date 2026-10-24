using System.Text.RegularExpressions;

namespace TenantFlow.Testing;

/// <summary>One reviewed exception to a security scan: <c>tool</c>, <c>id</c>, <c>target</c>, <c>reason</c>, <c>owner</c>, <c>expires</c>.</summary>
public sealed record ExceptionEntry(IReadOnlyDictionary<string, string> Fields)
{
    public string Tool => Fields.GetValueOrDefault("tool", string.Empty);

    public string Id => Fields.GetValueOrDefault("id", string.Empty);
}

/// <summary>
/// The register of reviewed exceptions to the security scans, <c>.config/security-exceptions.yaml</c> (SPEC-0009
/// criterion 13, ADR-0028 decision 3): a list under <c>exceptions:</c> of flat <c>key: value</c> maps. Read by the
/// architecture test that keeps it complete and current, and by the scans that skip what it names.
/// </summary>
public static partial class SecurityExceptions
{
    public static readonly string[] Tools = ["secrets", "dependencies", "images", "config", "iac"];

    public static IReadOnlyList<ExceptionEntry> Read()
    {
        var entries = new List<Dictionary<string, string>>();
        foreach (var raw in File.ReadAllLines(Path.Combine(DockerCli.RepositoryRoot, ".config", "security-exceptions.yaml")))
        {
            var line = TrailingComment().Replace(raw, string.Empty);
            if (EntryStart().Match(line) is { Success: true } start)
            {
                entries.Add(new(StringComparer.Ordinal) { [start.Groups["key"].Value] = Clean(start.Groups["value"].Value) });
            }
            else if (EntryField().Match(line) is { Success: true } field && entries.Count > 0)
            {
                entries[^1][field.Groups["key"].Value] = Clean(field.Groups["value"].Value);
            }
        }

        return [.. entries.Select(static entry => new ExceptionEntry(entry))];
    }

    /// <summary>The ids the register excuses for one tool.</summary>
    public static IReadOnlySet<string> IdsFor(string tool) =>
        Read().Where(entry => entry.Tool == tool && entry.Id.Length > 0).Select(static entry => entry.Id).ToHashSet(StringComparer.Ordinal);

    private static string Clean(string value) => value.Trim().Trim('"', '\'');

    [GeneratedRegex(@"\s+#.*$")]
    private static partial Regex TrailingComment();

    [GeneratedRegex(@"^\s*-\s+(?<key>\w+):\s*(?<value>.*)$")]
    private static partial Regex EntryStart();

    [GeneratedRegex(@"^\s+(?<key>\w+):\s*(?<value>.*)$")]
    private static partial Regex EntryField();
}
