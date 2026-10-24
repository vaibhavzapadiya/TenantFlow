using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TenantFlow.Architecture.Tests;

/// <summary>
/// ADR-0037: the local environment needs no script, checked on every build. <c>docker compose up -d --wait</c> from the
/// repository root starts it, the hosts' Development settings match the Compose defaults so they start straight after,
/// the committed local credentials are local-only values, and the checks are tests or Compose services.
/// </summary>
public sealed partial class LocalToolingTests
{
    private static readonly string Root = FindRoot();

    // The root compose.yaml is the one entry point; it includes the file that sits next to its configuration.
    [Fact]
    public void The_repository_root_compose_file_includes_the_local_environment()
    {
        var rootCompose = File.ReadAllText(Path.Combine(Root, "compose.yaml"));

        Assert.Contains("name: tenantflow", rootCompose, StringComparison.Ordinal);
        Assert.Contains("- deploy/compose/compose.yaml", rootCompose, StringComparison.Ordinal);
    }

    // "docker compose up -d", then F5: no step writes configuration, so the hosts' local settings must be the defaults.
    [Fact]
    public void Development_settings_match_the_compose_defaults()
    {
        var compose = File.ReadAllText(Path.Combine(Root, "deploy", "compose", "compose.yaml"));
        // A variable used more than once must have the same default everywhere.
        string Default(string name) => ComposeDefault().Matches(compose)
            .Where(match => match.Groups["name"].Value == name)
            .Select(static match => match.Groups["value"].Value)
            .Distinct()
            .Single();
        string Connection(string user, string password) =>
            $"Host=127.0.0.1;Port={Default("POSTGRES_PORT")};Database={Default("POSTGRES_DB")};Username={user};Password={password}";
        var keycloak = $"http://localhost:{Default("KEYCLOAK_PORT")}";

        var api = Settings("TenantFlow.Api");
        var worker = Settings("TenantFlow.Worker");
        var migrator = Settings("TenantFlow.Migrator");

        Assert.Equal(Connection("tenantflow_app", Default("TENANTFLOW_APP_PASSWORD")), (string?)api["ConnectionStrings"]?["TenantFlow"]);
        Assert.Equal($"{keycloak}/realms/tenantflow", (string?)api["Authentication"]?["Tenant"]?["Authority"]);
        Assert.Equal($"{keycloak}/realms/tenantflow-platform", (string?)api["Authentication"]?["Platform"]?["Authority"]);
        Assert.Equal(Connection("tenantflow_app", Default("TENANTFLOW_APP_PASSWORD")), (string?)worker["ConnectionStrings"]?["TenantFlow"]);
        Assert.Equal(Default("RABBITMQ_PORT"), worker["Messaging"]?["RabbitMq"]?["Port"]?.ToJsonString());
        Assert.Equal(Default("RABBITMQ_USER"), (string?)worker["Messaging"]?["RabbitMq"]?["Username"]);
        Assert.Equal(Default("RABBITMQ_PASSWORD"), (string?)worker["Messaging"]?["RabbitMq"]?["Password"]);
        Assert.Equal(Connection("tenantflow_owner", Default("TENANTFLOW_OWNER_PASSWORD")), (string?)migrator["ConnectionStrings"]?["TenantFlowOwner"]);
    }

    // ADR-0008 as amended: a committed credential is a throwaway local value, named so that a scanner or reviewer sees it.
    [Fact]
    public void Every_committed_local_password_is_a_local_only_value()
    {
        var compose = File.ReadAllText(Path.Combine(Root, "deploy", "compose", "compose.yaml"));
        var passwords = ComposeDefault().Matches(compose)
            .Where(static match => match.Groups["name"].Value.EndsWith("_PASSWORD", StringComparison.Ordinal))
            .Select(static match => (Name: match.Groups["name"].Value, Value: match.Groups["value"].Value))
            .Distinct()
            .ToList();

        Assert.NotEmpty(passwords);
        Assert.All(passwords, password => Assert.EndsWith("-local-only", password.Value, StringComparison.Ordinal));
    }

    // Nothing runs a shell script: setting up, checking, testing, the drills, the security scans and the progress
    // dashboard are Compose and dotnet commands, the same on every OS; provisioning is the Migrator's "provision" command
    // and releases use the official ECS actions (deploy/README.md).
    [Fact]
    public void No_host_or_shell_script_remains()
    {
        var scripts = Files(static path => HostScript().IsMatch(path));
        var shell = Files(static path => path.EndsWith(".sh", StringComparison.OrdinalIgnoreCase));

        Assert.Empty(scripts);
        Assert.Empty(shell);
    }

    // SPEC-0013 criterion 10: the choice between the host Docker CLI and Docker inside WSL is written once.
    [Fact]
    public void The_docker_or_wsl_choice_is_written_once()
    {
        var choosers = Files(static path => path.EndsWith(".cs", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(Path.Combine(Root, path)).Contains("\"wsl.exe\"", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(["tests/Shared/TenantFlow.Testing/DockerCli.cs"], choosers);
    }

    // The repository's own files, as paths relative to the root: no build output, IDE state, git data or local worktrees.
    private static List<string> Files(Func<string, bool> include) =>
        [.. Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Root, path).Replace('\\', '/'))
            .Where(static path => !Ignored().IsMatch(path))
            .Where(include)
            .Order(StringComparer.Ordinal)];

    [GeneratedRegex(@"\.(ps1|psm1|psd1|bat|cmd)$", RegexOptions.IgnoreCase)]
    private static partial Regex HostScript();

    [GeneratedRegex(@"(^|/)(\.git|\.claude|\.vs|bin|obj|artifacts|node_modules|\.storage|TestResults)/")]
    private static partial Regex Ignored();

    private static JsonNode Settings(string host) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "src", "Hosts", host, "appsettings.Development.json")))
        ?? throw new InvalidOperationException($"{host} has empty Development settings.");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TenantFlow.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    // A Compose variable with its default: ${NAME:-value}.
    [GeneratedRegex(@"\$\{(?<name>[A-Z_][A-Z0-9_]*):-(?<value>[^}]*)\}")]
    private static partial Regex ComposeDefault();
}
