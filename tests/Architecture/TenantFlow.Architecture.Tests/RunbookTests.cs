using System.Text.RegularExpressions;

namespace TenantFlow.Architecture.Tests;

/// <summary>
/// SPEC-0010 criterion 15: every runbook follows the blueprint section 22 template, every alert's runbook exists, and the
/// runbooks blueprint section 19 requires for M10 are there. Whether their queries return data is checked against the
/// drill stack at the milestone gate (docs/operations/drills).
/// </summary>
public sealed partial class RunbookTests
{
    private static readonly string Root = FindRoot();
    private static readonly string Runbooks = Path.Combine(Root, "docs", "operations", "runbooks");

    private static readonly string[] Template =
    [
        "## Symptoms and impact", "## Severity and escalation", "## Preconditions / access required",
        "## Dashboards and diagnostic queries", "## Immediate safe mitigations", "## Recovery procedure", "## Verification",
        "## Rollback / forward-fix", "## Customer / stakeholder communication", "## Follow-up actions",
    ];

    [Fact]
    public void Every_runbook_follows_the_section_22_template_and_the_section_19_set_is_complete()
    {
        var runbooks = Directory.GetFiles(Runbooks, "*.md").Where(static path => Path.GetFileName(path) != "README.md").ToList();

        Assert.Subset(
            runbooks.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal)!,
            new HashSet<string>(StringComparer.Ordinal)
            {
                "api-errors", "database-saturation-and-locks", "queue-backlog-and-poison-messages", "stuck-payment-and-reconciliation",
                "failed-import-export", "failed-migration", "suspected-cross-tenant-access",
            });
        Assert.All(runbooks, path =>
        {
            var lines = File.ReadAllLines(path);
            Assert.StartsWith("# Runbook: ", lines[0], StringComparison.Ordinal);
            Assert.Equal(Template, lines.Where(static line => line.StartsWith("## ", StringComparison.Ordinal)));
        });
    }

    [Fact]
    public void Every_alert_names_a_runbook_that_exists()
    {
        var rules = File.ReadAllText(Path.Combine(Root, "deploy", "observability", "prometheus", "rules", "alerts.rules.yml"));
        var paths = RunbookAnnotation().Matches(rules).Select(static m => m.Groups["path"].Value).Distinct().ToList();

        Assert.NotEmpty(paths);
        Assert.All(paths, path => Assert.True(File.Exists(Path.Combine(Root, path)), $"{path} does not exist"));
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

    [GeneratedRegex(@"runbook: (?<path>docs/operations/runbooks/[a-z-]+\.md)")]
    private static partial Regex RunbookAnnotation();
}
