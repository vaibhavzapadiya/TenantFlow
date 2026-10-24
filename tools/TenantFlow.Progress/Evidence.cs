using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TenantFlow.Progress;

/// <summary>
/// Live evidence from this checkout for the dashboard: git state, the spec, ADR and open-decision registers, the test
/// definitions, whether recorded commits exist, and the threat-model actions (SPEC-0009 criterion 1). Comparisons are
/// case-insensitive, as the registers and models have always been read.
/// </summary>
internal static partial class Evidence
{
    private static readonly string[] Owners = ["Claude", "user"];

    public static async Task<JsonObject> GitAsync(string root, CancellationToken cancellationToken)
    {
        var commits = new JsonArray();
        foreach (var line in (await Command.RunAsync(root, "git", ["log", "--format=%h|%cI|%s", "-n", "30"], cancellationToken)).Lines)
        {
            var parts = line.Split('|', 3);
            if (parts.Length == 3)
            {
                commits.Add(new JsonObject { ["hash"] = parts[0], ["date"] = parts[1], ["subject"] = parts[2] });
            }
        }

        return new JsonObject
        {
            ["branch"] = (await Command.RunAsync(root, "git", ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken)).FirstLine,
            ["head"] = (await Command.RunAsync(root, "git", ["rev-parse", "--short", "HEAD"], cancellationToken)).FirstLine,
            ["uncommitted"] = (await Command.RunAsync(root, "git", ["status", "--porcelain"], cancellationToken)).Lines.Count,
            ["commits"] = commits,
        };
    }

    public static JsonObject Registers(string root)
    {
        var specs = new JsonArray();
        foreach (var cells in MarkdownRows(Path.Combine(root, "docs", "specs", "README.md"), RegisterId()))
        {
            specs.Add(new JsonObject { ["id"] = RemoveLinks(cells[0]), ["title"] = cells[1], ["milestone"] = cells[2], ["status"] = cells[3] });
        }

        var adrs = new JsonArray();
        foreach (var cells in MarkdownRows(Path.Combine(root, "docs", "architecture", "decisions", "README.md"), RegisterId()))
        {
            adrs.Add(new JsonObject { ["id"] = RemoveLinks(cells[0]), ["title"] = cells[1], ["status"] = RemoveLinks(cells[2]) });
        }

        var decisions = new JsonArray();
        foreach (var cells in MarkdownRows(Path.Combine(root, "docs", "architecture", "open-decisions.md"), DecisionNumber()))
        {
            var status = RemoveLinks(cells[4]);
            decisions.Add(new JsonObject
            {
                ["n"] = int.Parse(cells[0], CultureInfo.InvariantCulture),
                ["decision"] = RemoveLinks(cells[1]),
                ["closeBy"] = cells[3],
                ["status"] = status,
                ["open"] = status.StartsWith("Open", StringComparison.OrdinalIgnoreCase),
            });
        }

        return new JsonObject { ["specs"] = specs, ["adrs"] = adrs, ["decisions"] = decisions };
    }

    /// <summary>Test methods defined in each test project ([Fact] and [Theory]); definitions, not results.</summary>
    public static JsonArray TestInventory(string root)
    {
        var inventory = new JsonArray();
        foreach (var project in Directory.GetFiles(Path.Combine(root, "tests"), "*.Tests.csproj", SearchOption.AllDirectories))
        {
            var count = SourceFiles(Path.GetDirectoryName(project)!).Sum(static file => TestAttribute().Count(File.ReadAllText(file)));
            inventory.Add(new JsonObject { ["project"] = Path.GetFileNameWithoutExtension(project), ["testMethods"] = count });
        }

        return inventory;
    }

    /// <summary>Which commit hashes recorded in progress.json exist in this repository.</summary>
    public static async Task<JsonObject> CommitsExistAsync(string root, JsonNode data, CancellationToken cancellationToken)
    {
        var result = new JsonObject();
        foreach (var slice in (data["milestones"]?.AsArray() ?? []).SelectMany(static milestone => milestone?["slices"]?.AsArray() ?? []))
        {
            if (slice?["commit"]?.GetValue<string>() is { Length: > 0 } commit && !result.ContainsKey(commit))
            {
                var found = await Command.RunAsync(root, "git", ["rev-parse", "--quiet", "--verify", $"{commit}^{{commit}}"], cancellationToken);
                result[commit] = found.Lines.Count > 0;
            }
        }

        return result;
    }

    /// <summary>
    /// Every row of every threat model's mitigation table, checked for completeness: a done row cites existing tests (or
    /// files); a planned row is not due in a closed milestone.
    /// </summary>
    public static JsonObject ThreatActions(string root, JsonNode data)
    {
        var closed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var milestone in data["milestones"]?.AsArray() ?? [])
        {
            var criteria = milestone?["exitCriteria"]?.AsArray() ?? [];
            if (criteria.Count > 0 && criteria.All(static criterion => string.Equals(criterion?["status"]?.GetValue<string>(), "verified", StringComparison.OrdinalIgnoreCase)))
            {
                closed.Add(milestone?["id"]?.GetValue<string>() ?? string.Empty);
            }
        }

        var testFiles = SourceFiles(Path.Combine(root, "tests"))
            .GroupBy(static file => Path.GetFileNameWithoutExtension(file), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var errors = new List<string>();
        var statuses = new List<string>();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var modelsDirectory = Path.Combine(root, "docs", "threat-models");
        var models = (Directory.Exists(modelsDirectory) ? Directory.GetFiles(modelsDirectory, "*.md") : [])
            .Where(static path => ModelName().IsMatch(Path.GetFileName(path)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (models.Count == 0)
        {
            errors.Add("No threat models found in docs/threat-models.");
        }

        foreach (var model in models)
        {
            var name = Path.GetFileName(model);
            var rows = MarkdownRows(model, ActionId()).ToList();
            if (rows.Count == 0)
            {
                errors.Add($"{name}: no action rows (| Id | Abuse case | Mitigation | Owner | Due | Status | Evidence |).");
                continue;
            }

            foreach (var cells in rows)
            {
                var id = cells[0];
                var where = $"{name} {id}";
                if (cells.Count != 7)
                {
                    errors.Add($"{where}: expected 7 cells, found {cells.Count}.");
                    continue;
                }

                if (seen.TryGetValue(id, out var first))
                {
                    errors.Add($"{where}: duplicate action id (also in {first}).");
                }
                else
                {
                    seen[id] = name;
                }

                var (owner, due, status, evidence) = (cells[3], cells[4], cells[5], cells[6]);
                if (cells[1].Length == 0 || cells[2].Length == 0)
                {
                    errors.Add($"{where}: abuse case or mitigation is empty.");
                }

                if (!Owners.Contains(owner, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add($"{where}: owner must be Claude or user, not '{owner}'.");
                }

                if (!DueMilestone().IsMatch(due))
                {
                    errors.Add($"{where}: due must be a milestone M0-M14, not '{due}'.");
                }

                if (evidence.Length == 0)
                {
                    errors.Add($"{where}: evidence is empty.");
                }

                var references = BacktickReference().Matches(evidence).Select(static match => match.Groups[1].Value).ToList();
                switch (status.ToLowerInvariant())
                {
                    case "done":
                        CheckDone(root, where, references, testFiles, errors);
                        break;
                    case "planned":
                        if (closed.Contains(due))
                        {
                            errors.Add($"{where}: planned for {due}, which is already closed.");
                        }

                        if (references.Count > 0)
                        {
                            errors.Add($"{where}: a planned action names where it will be delivered, without backticks.");
                        }

                        break;
                    case "accepted":
                        break;
                    default:
                        errors.Add($"{where}: status must be done, planned or accepted, not '{status}'.");
                        break;
                }

                statuses.Add(status.ToLowerInvariant());
            }
        }

        return new JsonObject
        {
            ["models"] = models.Count,
            ["total"] = statuses.Count,
            ["done"] = statuses.Count(static status => status == "done"),
            ["planned"] = statuses.Count(static status => status == "planned"),
            ["accepted"] = statuses.Count(static status => status == "accepted"),
            ["errors"] = new JsonArray([.. errors.Select(static error => (JsonNode?)error)]),
        };
    }

    // A done action cites at least one test that exists: "Class" or "Class.Method" under tests/. A cited file path that
    // exists is allowed but does not count as a test.
    private static void CheckDone(string root, string where, List<string> references, Dictionary<string, List<string>> testFiles, List<string> errors)
    {
        if (references.Count == 0)
        {
            errors.Add($"{where}: a done action must cite a test in backticks.");
        }

        var testCited = false;
        foreach (var reference in references)
        {
            if ((reference.Contains('/', StringComparison.Ordinal) || reference.Contains('\\', StringComparison.Ordinal)) && Path.Exists(Path.Combine(root, reference)))
            {
                continue;
            }

            var parts = reference.Split('.', 2);
            if (!testFiles.TryGetValue(parts[0], out var files))
            {
                errors.Add($"{where}: test class '{parts[0]}' does not exist under tests/.");
                continue;
            }

            if (parts.Length == 2)
            {
                var method = new Regex(@"\b" + Regex.Escape(parts[1]) + @"\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                if (!files.Any(file => method.IsMatch(File.ReadAllText(file))))
                {
                    errors.Add($"{where}: test method '{reference}' does not exist.");
                    continue;
                }
            }

            testCited = true;
        }

        if (references.Count > 0 && !testCited)
        {
            errors.Add($"{where}: a done action must cite at least one existing test.");
        }
    }

    // The rows of a Markdown table whose first cell matches; each row's trimmed cells.
    private static IEnumerable<List<string>> MarkdownRows(string file, Regex firstCell)
    {
        if (!File.Exists(file))
        {
            yield break;
        }

        foreach (var line in File.ReadLines(file))
        {
            if (!line.StartsWith('|'))
            {
                continue;
            }

            var cells = line.Trim().Trim('|').Split('|').Select(static cell => cell.Trim()).ToList();
            if (cells.Count > 0 && firstCell.IsMatch(cells[0]))
            {
                yield return cells;
            }
        }
    }

    private static string RemoveLinks(string text) => MarkdownLink().Replace(text, "$1").Replace("**", string.Empty, StringComparison.Ordinal).Trim();

    private static IEnumerable<string> SourceFiles(string directory) =>
        Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories).Where(static path => !BuildOutput().IsMatch(path));

    [GeneratedRegex(@"^\[\d{4}\]", RegexOptions.IgnoreCase)]
    private static partial Regex RegisterId();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex DecisionNumber();

    [GeneratedRegex(@"^[A-Z]{2}-A\d{2,3}$", RegexOptions.IgnoreCase)]
    private static partial Regex ActionId();

    [GeneratedRegex(@"^\d{4}-.+\.md$", RegexOptions.IgnoreCase)]
    private static partial Regex ModelName();

    [GeneratedRegex(@"^M(\d|1[0-4])$", RegexOptions.IgnoreCase)]
    private static partial Regex DueMilestone();

    [GeneratedRegex(@"\[(Fact|Theory)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TestAttribute();

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex BacktickReference();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"[\\/](bin|obj)[\\/]")]
    private static partial Regex BuildOutput();
}
