using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TenantFlow.Progress;

/// <summary>
/// "verify": the gate on the current commit (ADR-0037). The Release build fails on any
/// warning, formatting included; the test run then runs every test project of the solution with TRX results and
/// Cobertura coverage. Nothing counts as passed unless it ran and passed.
/// </summary>
internal static partial class QualityRun
{
    public static async Task<JsonObject> RunAsync(string root, string outDir, CancellationToken cancellationToken)
    {
        Program.WriteStep("Building in Release (warnings and formatting are errors)");
        var commit = (await Command.RunAsync(root, "git", ["rev-parse", "--short", "HEAD"], cancellationToken)).FirstLine;
        var dirty = (await Command.RunAsync(root, "git", ["status", "--porcelain"], cancellationToken)).Lines.Count;
        var build = await Command.RunAsync(root, "dotnet", ["build", "TenantFlow.sln", "--configuration", "Release"], cancellationToken);
        var passed = build.ExitCode == 0;
        int? warnings = null, errors = null;
        foreach (var line in build.Lines)
        {
            if (WarningCount().Match(line) is { Success: true } warning)
            {
                warnings = int.Parse(warning.Groups[1].Value, CultureInfo.InvariantCulture);
            }

            if (ErrorCount().Match(line) is { Success: true } error)
            {
                errors = int.Parse(error.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }

        string? failedStep = passed ? null : "Release build";
        var tests = new JsonArray();
        var coverage = new JsonArray();
        var architecturePassed = false;
        if (passed)
        {
            Program.WriteStep("Running tests with TRX results and Cobertura coverage");
            var results = Path.Combine(outDir, "test-results");
            if (Directory.Exists(results))
            {
                Directory.Delete(results, recursive: true);
            }

            var run = await Command.RunAsync(root, "dotnet", ["test", "TenantFlow.sln", "--no-build", "--configuration", "Release", "--logger", "trx", "--results-directory", results, "--collect", "Code Coverage;Format=cobertura"], cancellationToken);
            if (run.ExitCode != 0)
            {
                passed = false;
                failedStep = "Tests";
            }

            foreach (var trx in Directory.Exists(results) ? Directory.GetFiles(results, "*.trx") : [])
            {
                var document = XDocument.Load(trx);
                var ns = document.Root!.Name.Namespace;
                var summary = document.Root.Element(ns + "ResultSummary");
                var counters = summary?.Element(ns + "Counters");
                var codeBase = document.Root.Element(ns + "TestDefinitions")?.Element(ns + "UnitTest")?.Element(ns + "TestMethod")?.Attribute("codeBase")?.Value ?? string.Empty;
                var assembly = Path.GetFileNameWithoutExtension(codeBase);
                var failed = Count(counters, "failed");
                tests.Add(new JsonObject
                {
                    ["assembly"] = assembly,
                    ["total"] = Count(counters, "total"),
                    ["passed"] = Count(counters, "passed"),
                    ["failed"] = failed,
                    ["outcome"] = summary?.Attribute("outcome")?.Value,
                });
                if (assembly == "TenantFlow.Architecture.Tests" && failed == 0 && Count(counters, "total") > 0)
                {
                    architecturePassed = true;
                }
            }

            var rates = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var file in Directory.Exists(results) ? Directory.GetFiles(results, "*.cobertura.xml", SearchOption.AllDirectories) : [])
            {
                foreach (var package in XDocument.Load(file).Descendants("package"))
                {
                    var name = package.Attribute("name")?.Value ?? string.Empty;
                    var rate = double.Parse(package.Attribute("line-rate")?.Value ?? "0", CultureInfo.InvariantCulture);
                    if (!rates.TryGetValue(name, out var best) || best < rate)
                    {
                        rates[name] = rate;
                    }
                }
            }

            foreach (var (name, rate) in rates.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                if (name.StartsWith("TenantFlow.", StringComparison.Ordinal) && !name.Contains("Tests", StringComparison.Ordinal) && name != "TenantFlow.Testing")
                {
                    coverage.Add(new JsonObject { ["assembly"] = name, ["lineRate"] = Math.Round(rate * 100, 1) });
                }
            }
        }

        var quality = new JsonObject
        {
            ["ranAt"] = Program.Now(),
            ["commit"] = commit,
            ["uncommittedChanges"] = dirty,
            ["verify"] = new JsonObject { ["passed"] = passed, ["warnings"] = warnings, ["errors"] = errors, ["failedStep"] = failedStep },
            ["tests"] = tests,
            ["coverage"] = coverage,
            // The Compose rules are architecture tests (ComposeFileTests).
            ["composeValid"] = architecturePassed,
        };
        Program.WriteStep($"Quality run {(passed && architecturePassed ? "passed" : "FAILED")} for commit {commit} (recorded in artifacts/progress/quality.json)");
        return quality;
    }

    private static int Count(XElement? counters, string name) =>
        int.Parse(counters?.Attribute(name)?.Value ?? "0", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^\s*(\d+) Warning\(s\)")]
    private static partial Regex WarningCount();

    [GeneratedRegex(@"^\s*(\d+) Error\(s\)")]
    private static partial Regex ErrorCount();
}
