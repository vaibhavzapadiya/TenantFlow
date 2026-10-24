using System.Text.RegularExpressions;

namespace TenantFlow.Architecture.Tests;

/// <summary>
/// SPEC-0012 criteria 9 to 11 (S4): how the release pipeline is wired, checked on every build while it cannot run (no
/// remote yet). Images are built once from main and promoted by digest; every AWS call uses an OIDC role session; prod
/// gets only what staging ran; the Migrator runs, and the previous version is checked, before any service rolls.
/// </summary>
public sealed partial class ReleaseWorkflowTests
{
    private static readonly string Root = FindRoot();
    private static readonly string[] Images = ["api", "worker", "migrator"];

    // Keycloak is built and attested with every release too, but rolled out by the operator (keycloak_image_tag).
    private static readonly string[] BuiltImages = [.. Images, "keycloak"];

    // The services a release rolls, as their steps name them.
    private static readonly string[] RolledServices = ["Worker", "API"];

    private static string Release { get; } = Read(".github", "workflows", "release.yml");

    private static string Deploy { get; } = Read(".github", "workflows", "deploy.yml");

    [Fact]
    public void Both_workflows_grant_nothing_by_default_and_pin_every_action_by_commit()
    {
        Assert.All([Release, Deploy], workflow =>
        {
            Assert.Contains("\npermissions: {}\n", workflow, StringComparison.Ordinal);
            var uses = Uses().Matches(workflow).Select(static m => m.Groups["ref"].Value).Where(static r => !r.StartsWith("./", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(uses);
            Assert.All(uses, use => Assert.Matches("@[0-9a-f]{40}$", use));
        });
    }

    [Fact]
    public void Images_are_built_only_after_ci_passes_for_a_push_to_main_with_an_sbom_and_provenance()
    {
        Assert.Contains("  workflow_run:\n    workflows: [ci]\n    types: [completed]\n    branches: [main]\n", Release, StringComparison.Ordinal);
        Assert.Contains("if: github.event.workflow_run.conclusion == 'success' && github.event.workflow_run.event == 'push'", Release, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request", Release, StringComparison.Ordinal);

        Assert.All(BuiltImages, image =>
        {
            var step = Release[Release.IndexOf($"- name: Build and push {image}\n", StringComparison.Ordinal)..];
            step = step[..step.IndexOf("\n\n", StringComparison.Ordinal)];
            Assert.Contains($"target: {image}\n", step, StringComparison.Ordinal);
            Assert.Contains("sbom: true\n", step, StringComparison.Ordinal);
            Assert.Contains("provenance: mode=max\n", step, StringComparison.Ordinal);
            Assert.Contains(":${{ github.event.workflow_run.head_sha }}", step, StringComparison.Ordinal);
            Assert.Contains($"subject-digest: ${{{{ steps.{image}.outputs.digest }}}}", Release, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void No_aws_access_key_is_used_and_each_role_trusts_only_its_own_subject()
    {
        Assert.All([Release, Deploy], static workflow =>
        {
            Assert.DoesNotContain("aws-access-key-id", workflow, StringComparison.Ordinal);
            Assert.DoesNotContain("AWS_SECRET_ACCESS_KEY", workflow, StringComparison.Ordinal);
        });

        Assert.Contains("environment: ${{ inputs.environment }}\n", Deploy, StringComparison.Ordinal);
        Assert.Contains("\"repo:${var.github_repository}:ref:refs/heads/main\"", Read("deploy", "terraform", "shared", "main.tf"), StringComparison.Ordinal);
        Assert.Contains("\"repo:${var.github_repository}:environment:${var.environment}\"", Read("deploy", "terraform", "environment", "iam.tf"), StringComparison.Ordinal);
    }

    [Fact]
    public void Prod_runs_after_staging_with_the_same_digests()
    {
        var staging = Job(Release, "staging");
        var prod = Job(Release, "prod");

        Assert.Contains("needs: [build, staging]\n", prod, StringComparison.Ordinal);
        Assert.Contains("environment: prod\n", prod, StringComparison.Ordinal);
        Assert.All(Images, image =>
        {
            var input = $"{image}: ${{{{ needs.build.outputs.{image} }}}}\n";
            Assert.Contains(input, staging, StringComparison.Ordinal);
            Assert.Contains(input, prod, StringComparison.Ordinal);
        });
        Assert.All(Images, image => Assert.Contains($"echo \"{image}=$REGISTRY/tenantflow/{image}@$", Release, StringComparison.Ordinal));
    }

    [Fact]
    public void A_deployment_verifies_provenance_then_migrates_then_checks_the_previous_version_before_rolling()
    {
        string[] order =
        [
            "- name: Verify build provenance",
            "- name: Migrate",
            "- name: Previous version passes on the new schema",
            "- name: Roll the Worker",
            "- name: Roll the API",
            "- name: New version is ready",
        ];

        var positions = order.Select(step => Deploy.IndexOf(step, StringComparison.Ordinal)).ToList();

        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Order(), positions);
    }

    // The rollout is AWS's own actions, not a script: each replaces only its named container's image in the latest
    // revision, the Migrator must exit 0, and a service must reach a stable deployment (or fail the job).
    [Fact]
    public void Deployments_use_the_official_ecs_actions_and_wait_for_their_outcome()
    {
        Assert.DoesNotContain("release.sh", Deploy, StringComparison.Ordinal);
        Assert.All(Images, image =>
        {
            var render = Step(Deploy, $"id: {image}\n");
            Assert.Contains("aws-actions/amazon-ecs-render-task-definition@", render, StringComparison.Ordinal);
            Assert.Contains($"task-definition-family: ${{{{ vars.ECS_CLUSTER }}}}-{image}\n", render, StringComparison.Ordinal);
            Assert.Contains($"container-name: {image}\n", render, StringComparison.Ordinal);
            Assert.EndsWith($"image: ${{{{ inputs.{image} }}}}", render, StringComparison.Ordinal);
        });

        var migrate = Step(Deploy, "- name: Migrate\n");
        Assert.Contains("aws-actions/amazon-ecs-deploy-task-definition@", migrate, StringComparison.Ordinal);
        Assert.Contains("run-task: true\n", migrate, StringComparison.Ordinal);
        Assert.Contains("wait-for-task-stopped: true\n", migrate, StringComparison.Ordinal);
        Assert.DoesNotContain("service:", migrate, StringComparison.Ordinal);
        Assert.All(RolledServices, name =>
        {
            var roll = Step(Deploy, $"- name: Roll the {name}\n");
            Assert.Contains("aws-actions/amazon-ecs-deploy-task-definition@", roll, StringComparison.Ordinal);
            Assert.Contains("wait-for-service-stability: true\n", roll, StringComparison.Ordinal);
        });
    }

    // The text of one step, from the given line to the next blank line.
    private static string Step(string workflow, string marker)
    {
        var start = workflow.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no step with {marker.Trim()}");
        var end = workflow.IndexOf("\n\n", start, StringComparison.Ordinal);
        return workflow[start..(end < 0 ? workflow.Length : end)];
    }

    private static string Job(string workflow, string name)
    {
        var start = workflow.IndexOf($"\n  {name}:\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"no job named {name}");
        var next = JobStart().Match(workflow, start + name.Length + 4);
        return workflow[start..(next.Success ? next.Index : workflow.Length)];
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([Root, .. path])).ReplaceLineEndings("\n");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TenantFlow.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    [GeneratedRegex(@"uses:\s*(?<ref>\S+)")]
    private static partial Regex Uses();

    [GeneratedRegex(@"\n  [a-z][\w-]*:\n")]
    private static partial Regex JobStart();
}
