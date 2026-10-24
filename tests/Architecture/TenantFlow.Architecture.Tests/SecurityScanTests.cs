using System.Globalization;
using System.Text.RegularExpressions;

namespace TenantFlow.Architecture.Tests;

/// <summary>
/// SPEC-0009 criteria 13 and 14 (ADR-0028): how the security scan is wired, checked on every build. The scans run in
/// <c>tests/Security</c> (and CI's <c>security</c> job); this test keeps their scanners pinned, the exceptions register
/// reviewed and current, and the CI job read-only.
/// </summary>
public sealed partial class SecurityScanTests
{
    private static readonly string Root = DockerCli.RepositoryRoot;
    private static readonly string[] RequiredFields = ["tool", "id", "reason", "owner", "expires"];

    [Fact]
    public void Every_scanner_image_is_pinned_by_digest()
    {
        var scans = File.ReadAllText(Path.Combine(Root, "tests", "Security", "TenantFlow.Security.Tests", "ScanWorkspace.cs"));

        var images = ImageConstant().Matches(scans).Select(static m => m.Groups["image"].Value).ToList();

        Assert.Equal(2, images.Count);
        Assert.All(images, image => Assert.Matches("@sha256:[0-9a-f]{64}$", image));
    }

    [Fact]
    public void Every_exception_names_its_tool_id_reason_and_owner_and_has_not_expired()
    {
        var entries = SecurityExceptions.Read();

        var today = DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime);
        Assert.NotEmpty(entries);
        Assert.All(entries, entry =>
        {
            foreach (var key in RequiredFields)
            {
                Assert.True(entry.Fields.TryGetValue(key, out var value) && value.Length > 0, $"an exception has no {key}");
            }

            Assert.Contains(entry.Tool, SecurityExceptions.Tools);
            Assert.True(DateOnly.ParseExact(entry.Fields["expires"], "yyyy-MM-dd", CultureInfo.InvariantCulture) >= today, $"{entry.Tool}/{entry.Id} has expired");
        });
    }

    [Fact]
    public void The_ci_security_job_runs_the_scan_read_only_with_actions_pinned_by_commit()
    {
        var workflow = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "ci.yml"));
        var job = workflow[workflow.IndexOf("\n  security:", StringComparison.Ordinal)..];

        Assert.Contains("permissions:\n      contents: read", job.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("run: dotnet run --project tests/Security/TenantFlow.Security.Tests -c Release", job, StringComparison.Ordinal);
        Assert.Contains("fetch-depth: 0", job, StringComparison.Ordinal);
        Assert.All(Uses().Matches(job), use => Assert.Matches("@[0-9a-f]{40}$", use.Groups["ref"].Value));
    }

    [GeneratedRegex(@"public const string \w+Image = ""(?<image>[^""]+)""")]
    private static partial Regex ImageConstant();

    [GeneratedRegex(@"uses:\s*(?<ref>\S+)")]
    private static partial Regex Uses();
}
