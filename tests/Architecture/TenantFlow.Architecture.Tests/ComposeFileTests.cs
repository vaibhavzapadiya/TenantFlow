using System.Text.Json;
using System.Text.RegularExpressions;

namespace TenantFlow.Architecture.Tests;

/// <summary>
/// The rules of the local environment's Compose file and realms (SPEC-0001, SPEC-0002 criterion 11, ADR-0003, ADR-0037),
/// checked on every build without Docker. That the file resolves is checked by CI with <c>docker compose config</c>.
/// </summary>
public sealed partial class ComposeFileTests
{
    private const string BuiltImage = "tenantflow-migrator:local";
    private static readonly string Root = FindRoot();
    // Without comment lines: comments may name a variable or an image.
    private static readonly string Compose = string.Join('\n', File.ReadAllLines(Path.Combine(Root, "deploy", "compose", "compose.yaml"))
        .Where(static line => !line.TrimStart().StartsWith('#')));

    // ADR-0003: a tag cannot move under us. The one image built here comes from deploy/docker/Dockerfile, whose base
    // images are pinned there (ContainerImageTests).
    [Fact]
    public void Every_image_is_pinned_to_a_version_and_digest_except_the_one_built_here()
    {
        var images = ImageLine().Matches(Compose).Select(static match => match.Groups["image"].Value).ToList();

        Assert.NotEmpty(images);
        Assert.All(images.Where(static image => image != BuiltImage), image => Assert.Matches(PinnedImage(), image));
        Assert.Contains("target: migrator", Compose, StringComparison.Ordinal);
    }

    // ADR-0029: the rule checks run on exactly the Prometheus and Alertmanager that the stack runs.
    [Theory]
    [InlineData("prometheus", "rule-tests")]
    [InlineData("alertmanager", "alertmanager-check")]
    public void A_check_uses_the_image_of_the_service_it_checks(string service, string check)
    {
        Assert.Equal(ImageOf(service), ImageOf(check));
    }

    // SPEC-0001: nothing is reachable from other machines on the network.
    [Fact]
    public void Every_published_port_binds_to_the_loopback_address()
    {
        var ports = PortMapping().Matches(Compose).Select(static match => match.Groups["mapping"].Value).ToList();

        Assert.Equal(11, ports.Count);
        Assert.All(ports, port => Assert.StartsWith("127.0.0.1:", port, StringComparison.Ordinal));
        Assert.DoesNotContain("network_mode", Compose, StringComparison.Ordinal);
    }

    // ADR-0037: a fresh clone needs no .env, so every variable has its local-only default.
    [Fact]
    public void Every_variable_has_a_default()
    {
        var withoutDefault = VariableWithoutDefault().Matches(Compose).Select(static match => match.Value).ToList();

        Assert.Empty(withoutDefault);
    }

    // SPEC-0002 criterion 11: the realms hold only synthetic users, and their only credential is the placeholder that
    // Keycloak fills in from its environment at import; no secret or literal password is committed.
    [Theory]
    [InlineData("tenantflow")]
    [InlineData("tenantflow-platform")]
    public void The_realm_holds_only_synthetic_users_with_the_placeholder_password(string realm)
    {
        var text = File.ReadAllText(Path.Combine(Root, "deploy", "identity", "realms", "local", $"{realm}-realm.json"));
        using var document = JsonDocument.Parse(text);

        Assert.DoesNotMatch(CredentialProperty(), text);
        var users = document.RootElement.GetProperty("users").EnumerateArray().ToList();
        Assert.NotEmpty(users);
        foreach (var user in users)
        {
            Assert.StartsWith("dev-", user.GetProperty("username").GetString(), StringComparison.Ordinal);
            Assert.EndsWith("@tenantflow.test", user.GetProperty("email").GetString(), StringComparison.Ordinal);
            var credential = Assert.Single(user.GetProperty("credentials").EnumerateArray());
            Assert.Equal("${KEYCLOAK_DEV_USER_PASSWORD}", credential.GetProperty("value").GetString());
        }

        Assert.All(CredentialValue().Matches(text), value => Assert.Equal("${KEYCLOAK_DEV_USER_PASSWORD}", value.Groups["value"].Value));
    }

    // The image line of one top-level service (services are indented by two spaces, their keys by four).
    private static string ImageOf(string service)
    {
        var start = Compose.IndexOf($"\n  {service}:\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"compose.yaml has no service {service}");
        return ImageLine().Match(Compose, start).Groups["image"].Value;
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

    [GeneratedRegex(@"(?m)^\s+image:\s*(?<image>\S+)\s*$")]
    private static partial Regex ImageLine();

    [GeneratedRegex(@"^[^:@/\s]+(/[^:@/\s]+)*:[^:@/\s]+@sha256:[0-9a-f]{64}$")]
    private static partial Regex PinnedImage();

    // Short-syntax port mappings, the only form the file uses: - "host:port:container".
    [GeneratedRegex(@"(?m)^\s+-\s*""(?<mapping>[^""]+:\d+)""\s*$")]
    private static partial Regex PortMapping();

    // ${NAME} or ${NAME:?message}, but not $${...} (a container-side variable) and not ${NAME:-default}.
    [GeneratedRegex(@"(?<!\$)\$\{(?![A-Z_][A-Z0-9_]*:-)[^}]*\}")]
    private static partial Regex VariableWithoutDefault();

    [GeneratedRegex(@"""(secret|clientSecret|password|hashedSaltedValue|secretData|credentialData)""\s*:")]
    private static partial Regex CredentialProperty();

    [GeneratedRegex(@"""value""\s*:\s*""(?<value>[^""]*)""")]
    private static partial Regex CredentialValue();
}
