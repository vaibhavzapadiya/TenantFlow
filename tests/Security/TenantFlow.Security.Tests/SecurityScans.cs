using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TenantFlow.Security.Tests;

/// <summary>
/// SPEC-0009 criterion 13 (ADR-0028): the secret, dependency, image and infrastructure scans. A finding fails its test
/// unless the exceptions register names it; a scanner that cannot run or reach its feed fails too, never passes
/// silently. Messages name the rule and the place, never a secret value.
/// </summary>
public sealed partial class SecurityScans
{
    private static readonly string[] Common = ["--redact", "--no-banner", "--exit-code", "1", "--report-format", "json"];

    // Secrets over every ref of the history: a mirror clone is a standalone repository, so a git worktree or a CI checkout
    // scans the same way. --no-local packs the objects instead of copying object files, which some file systems refuse.
    [Fact]
    public async Task No_secret_in_the_git_history()
    {
        using var workspace = new ScanWorkspace();
        var clone = await DockerCli.StartAsync("git", ["clone", "--quiet", "--mirror", "--no-local", DockerCli.RepositoryRoot, Path.Combine(workspace.Folder, "history.git")], Cancel);
        Assert.True(clone.ExitCode == 0, "The repository could not be cloned for scanning.");

        var findings = await GitleaksAsync(workspace, ["git", "/scan/history.git", "--log-opts=--all", "--report-path", "/scan/report.json"], "history");

        Assert.Empty(findings);
    }

    // Secrets in the working tree as git sees it: tracked files and untracked files that are not ignored, copied out
    // first, so a local .env is never read.
    [Fact]
    public async Task No_secret_in_the_working_tree()
    {
        using var workspace = new ScanWorkspace();
        var files = await DockerCli.StartAsync("git", ["ls-files", "--cached", "--others", "--exclude-standard"], Cancel);
        Assert.True(files.ExitCode == 0, "git ls-files failed.");
        workspace.Copy(files.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), "tree");

        var findings = await GitleaksAsync(workspace, ["dir", "/scan/tree", "--report-path", "/scan/report.json"], "tree");

        Assert.Empty(findings);
    }

    // Vulnerable packages, transitive ones included; moderate and above fail (the build already fails on high and critical).
    [Fact]
    public async Task No_vulnerable_package_at_moderate_or_above()
    {
        var listed = await DockerCli.StartAsync("dotnet", ["list", "TenantFlow.sln", "package", "--vulnerable", "--include-transitive", "--format", "json", "--output-version", "1"], Cancel);
        Assert.True(listed.ExitCode == 0, "The vulnerability query failed (restore first; is the feed reachable?).");
        var ignored = SecurityExceptions.IdsFor("dependencies");

        var findings = (JsonNode.Parse(listed.Output)?["projects"]?.AsArray() ?? [])
            .SelectMany(static project => project?["frameworks"]?.AsArray() ?? [])
            .SelectMany(static framework => (framework?["topLevelPackages"]?.AsArray() ?? []).Concat(framework?["transitivePackages"]?.AsArray() ?? []))
            .SelectMany(static package => (package?["vulnerabilities"]?.AsArray() ?? []).Select(vulnerability => (Package: package, Vulnerability: vulnerability)))
            .Where(static found => found.Vulnerability?["severity"]?.GetValue<string>() is "Moderate" or "High" or "Critical")
            .Select(static found => (found.Package, found.Vulnerability, Id: found.Vulnerability?["advisoryurl"]?.GetValue<string>()?.Split('/')[^1] ?? string.Empty))
            .Where(found => !ignored.Contains(found.Id))
            .Select(static found => $"{found.Package?["id"]} {found.Package?["resolvedVersion"]}: {found.Vulnerability?["severity"]} {found.Id}")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(findings);
    }

    public static TheoryData<string> ComposeImages()
    {
        // Every pulled image of the local environment; the Migrator is built from deploy/docker/Dockerfile, whose base
        // images are scanned where the deployed images are built.
        var compose = File.ReadAllText(Path.Combine(DockerCli.RepositoryRoot, "deploy", "compose", "compose.yaml"));
        return [.. ImageLine().Matches(compose).Select(static match => match.Groups["image"].Value).Where(static image => image.Contains("@sha256:", StringComparison.Ordinal)).Distinct().Order(StringComparer.Ordinal)];
    }

    // HIGH or CRITICAL vulnerabilities that have a fix fail.
    [Theory]
    [MemberData(nameof(ComposeImages))]
    public async Task The_image_has_no_fixed_high_or_critical_vulnerability(string image)
    {
        using var workspace = new ScanWorkspace();
        await File.WriteAllLinesAsync(Path.Combine(workspace.Folder, ".trivyignore"), SecurityExceptions.IdsFor("images"), Cancel);

        var scan = await DockerCli.RunAsync(
            [
                "run", "--rm", .. await workspace.MountsAsync(writable: false, Cancel), ScanWorkspace.TrivyImage, "image", .. ScanWorkspace.TrivyRepositories,
                "--scanners", "vuln", "--severity", "HIGH,CRITICAL", "--ignore-unfixed", "--ignorefile", "/scan/.trivyignore",
                "--exit-code", "1", "--no-progress", "--skip-version-check", "--quiet", "--timeout", "15m", image,
            ],
            Cancel);

        Assert.True(scan.ExitCode == 0, $"{image} has fixed HIGH or CRITICAL vulnerabilities, or could not be scanned (exit {scan.ExitCode}):\n{scan.Output}{scan.Error}");
    }

    // Trivy's misconfiguration checks over the infrastructure code; HIGH or CRITICAL fail. Only the configuration is
    // copied: provider caches under .terraform are binaries, and local state would be a secret.
    [Fact]
    public async Task No_high_or_critical_misconfiguration_in_the_infrastructure_code()
    {
        using var workspace = new ScanWorkspace();
        var files = Directory.GetFiles(Path.Combine(DockerCli.RepositoryRoot, "deploy"), "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(DockerCli.RepositoryRoot, path).Replace('\\', '/'))
            .Where(static path => InfrastructureFolder().IsMatch(path) && !path.Contains("/.terraform/", StringComparison.Ordinal) && !path.Contains(".tfstate", StringComparison.Ordinal));
        workspace.Copy(files, "iac");
        await File.WriteAllLinesAsync(Path.Combine(workspace.Folder, ".trivyignore-iac"), SecurityExceptions.IdsFor("iac"), Cancel);

        var scan = await DockerCli.RunAsync(
            [
                "run", "--rm", .. await workspace.MountsAsync(writable: false, Cancel), ScanWorkspace.TrivyImage, "config", "--severity", "HIGH,CRITICAL",
                "--ignorefile", "/scan/.trivyignore-iac", "--exit-code", "1", "--skip-version-check", "--quiet", "/scan/iac/deploy",
            ],
            Cancel);

        Assert.True(scan.ExitCode == 0, $"HIGH or CRITICAL misconfigurations in deploy/terraform, deploy/docker or deploy/keycloak, or the scan failed (exit {scan.ExitCode}):\n{scan.Output}{scan.Error}");
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // Runs gitleaks over /scan and returns the findings the register does not name: "rule:path" (stable across commits
    // and line moves) or one exact fingerprint. Redacted: the rule and the place, never the value.
    private static async Task<List<string>> GitleaksAsync(ScanWorkspace workspace, string[] arguments, string what)
    {
        var git = new[] { "-e", "GIT_CONFIG_COUNT=1", "-e", "GIT_CONFIG_KEY_0=safe.directory", "-e", "GIT_CONFIG_VALUE_0=*" };
        var scan = await DockerCli.RunAsync(["run", "--rm", .. await workspace.MountsAsync(writable: true, Cancel), .. git, ScanWorkspace.GitleaksImage, .. arguments, .. Common], Cancel);
        var report = Path.Combine(workspace.Folder, "report.json");
        Assert.True(File.Exists(report), $"gitleaks did not run over the {what} (exit {scan.ExitCode}): {scan.Error}");

        var ignored = SecurityExceptions.IdsFor("secrets");
        return [.. (JsonNode.Parse(await File.ReadAllTextAsync(report, Cancel))?.AsArray() ?? [])
            .Select(static finding => (
                Rule: finding?["RuleID"]?.GetValue<string>() ?? string.Empty,
                Path: (finding?["File"]?.GetValue<string>() ?? string.Empty).Replace("/scan/tree/", string.Empty, StringComparison.Ordinal),
                Line: finding?["StartLine"]?.GetValue<int>() ?? 0,
                Commit: finding?["Commit"]?.GetValue<string>() ?? string.Empty,
                Fingerprint: finding?["Fingerprint"]?.GetValue<string>() ?? string.Empty))
            .Where(finding => !ignored.Contains($"{finding.Rule}:{finding.Path}") && !ignored.Contains(finding.Fingerprint))
            .Select(finding => $"{finding.Rule} in {finding.Path}:{finding.Line}{(finding.Commit.Length >= 8 ? $" commit {finding.Commit[..8]}" : string.Empty)} (exception id {finding.Rule}:{finding.Path})")];
    }

    [GeneratedRegex(@"(?m)^\s+image:\s*(?<image>\S+)\s*$")]
    private static partial Regex ImageLine();

    [GeneratedRegex(@"^deploy/(terraform|docker|keycloak)/")]
    private static partial Regex InfrastructureFolder();
}
