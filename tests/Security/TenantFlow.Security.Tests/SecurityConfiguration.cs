using System.Text.RegularExpressions;

namespace TenantFlow.Security.Tests;

/// <summary>
/// The configuration part of the security scan (SPEC-0009 criterion 13, ADR-0028), without Docker: no Compose
/// service is privileged, shares a host namespace, adds capabilities or mounts the Docker socket, and the GitHub
/// workflows pin their actions by commit, grant no write permission and check out without persisted credentials.
/// Pinned images and loopback-only ports are architecture tests (ComposeFileTests). A finding the exceptions register names
/// (tool "config") is allowed.
/// </summary>
public sealed partial class SecurityConfiguration
{
    private static readonly string Root = DockerCli.RepositoryRoot;
    private static readonly IReadOnlySet<string> Excused = SecurityExceptions.IdsFor("config");

    [Fact]
    public void No_compose_service_is_privileged_shares_a_host_namespace_adds_capabilities_or_mounts_the_docker_socket()
    {
        var findings = new List<string>();
        var service = string.Empty;
        foreach (var raw in File.ReadAllLines(Path.Combine(Root, "deploy", "compose", "compose.yaml")))
        {
            var line = TrailingComment().Replace(raw, string.Empty);
            if (ServiceName().Match(line) is { Success: true } name)
            {
                service = name.Groups["name"].Value;
            }
            else if (PrivilegedTrue().IsMatch(line))
            {
                findings.Add($"compose:{service}:privileged");
            }
            else if (HostNamespace().Match(line) is { Success: true } mode)
            {
                findings.Add($"compose:{service}:{mode.Groups["mode"].Value}_host");
            }
            else if (CapAdd().IsMatch(line))
            {
                findings.Add($"compose:{service}:cap_add");
            }
            else if (line.Contains("docker.sock", StringComparison.Ordinal))
            {
                findings.Add($"compose:{service}:docker_socket");
            }
        }

        var unexcused = findings.Where(finding => !Excused.Contains(finding)).ToList();

        Assert.Empty(unexcused);
    }

    [Fact]
    public void Every_workflow_pins_actions_grants_no_write_permission_and_does_not_persist_credentials()
    {
        var findings = new List<string>();
        foreach (var workflow in Directory.GetFiles(Path.Combine(Root, ".github", "workflows"), "*.y*ml"))
        {
            var name = Path.GetFileName(workflow);
            var lines = File.ReadAllLines(workflow).Select(line => TrailingComment().Replace(line, string.Empty)).ToArray();
            for (var i = 0; i < lines.Length; i++)
            {
                if (Uses().Match(lines[i]) is { Success: true } use && !use.Groups["action"].Value.StartsWith("./", StringComparison.Ordinal) && !CommitSha().IsMatch(use.Groups["ref"].Value))
                {
                    findings.Add($"workflow:{name}:action_not_pinned:{use.Groups["action"].Value}");
                }

                if (WriteAll().IsMatch(lines[i]) || WritePermission().IsMatch(lines[i]))
                {
                    findings.Add($"workflow:{name}:write_permission:line{i + 1}");
                }

                if (lines[i].Contains("uses: actions/checkout@", StringComparison.Ordinal)
                    && !string.Join('\n', lines.Skip(i + 1).Take(4)).Contains("persist-credentials: false", StringComparison.Ordinal))
                {
                    findings.Add($"workflow:{name}:checkout_persists_credentials:line{i + 1}");
                }
            }
        }

        var unexcused = findings.Where(finding => !Excused.Contains(finding)).ToList();

        Assert.Empty(unexcused);
    }

    [GeneratedRegex(@"\s+#.*$")]
    private static partial Regex TrailingComment();

    // A top-level service: two spaces, a name, a colon, nothing after it.
    [GeneratedRegex(@"^  (?<name>[a-z][a-z0-9-]*):\s*$")]
    private static partial Regex ServiceName();

    [GeneratedRegex(@"^\s+privileged:\s*true\s*$")]
    private static partial Regex PrivilegedTrue();

    [GeneratedRegex(@"^\s+(?<mode>network_mode|pid|ipc):\s*""?host""?\s*$")]
    private static partial Regex HostNamespace();

    [GeneratedRegex(@"^\s+cap_add:")]
    private static partial Regex CapAdd();

    [GeneratedRegex(@"^\s*-?\s*uses:\s*(?<action>[^@\s]+)@(?<ref>\S+)")]
    private static partial Regex Uses();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex CommitSha();

    [GeneratedRegex(@"^\s*permissions:\s*write-all")]
    private static partial Regex WriteAll();

    [GeneratedRegex(@"^\s+[\w-]+:\s*write\s*$")]
    private static partial Regex WritePermission();
}
