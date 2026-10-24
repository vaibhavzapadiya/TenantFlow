using System.Text.RegularExpressions;

namespace TenantFlow.Architecture.Tests;

/// <summary>
/// SPEC-0012 S3d: how the images are built, checked on every build. A local secret never enters the build context,
/// every deployed process runs as a non-root user on a chiseled base, and the database connection the deployment
/// configures verifies the server against the certificate authority the images ship.
/// </summary>
public sealed partial class ContainerImageTests
{
    private static readonly string Root = FindRoot();
    private static readonly string[] FinalStages = ["api", "worker", "migrator"];
    private static readonly string[] AllowedInContext =
    [
        "src/", "Directory.Build.props", "Directory.Packages.props", "global.json", "nuget.config", ".editorconfig", "deploy/certs/",
        "deploy/keycloak/Dockerfile", "deploy/identity/realms/production/",
    ];

    [Fact]
    public void The_build_context_denies_everything_and_lets_in_only_the_sources()
    {
        var lines = File.ReadAllLines(Path.Combine(Root, ".dockerignore"))
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();

        Assert.Equal("*", lines[0]);
        Assert.All(lines.Where(static line => line.StartsWith('!')), line => Assert.Contains(line[1..], AllowedInContext));
    }

    [Fact]
    public void Every_final_image_runs_as_the_non_root_user_on_a_chiseled_base()
    {
        var dockerfile = File.ReadAllText(Path.Combine(Root, "deploy", "docker", "Dockerfile")).ReplaceLineEndings("\n");
        var baseImages = BaseImageArgument().Matches(dockerfile).ToDictionary(static m => m.Groups["name"].Value, static m => m.Groups["image"].Value);

        Assert.All(FinalStages, stage =>
        {
            var body = StageBody(dockerfile, stage);
            Assert.Contains("chiseled", baseImages[BaseImageOf(dockerfile, stage)], StringComparison.Ordinal);
            Assert.Contains("\nUSER $APP_UID\n", body, StringComparison.Ordinal);
            Assert.True(body.IndexOf("\nUSER $APP_UID\n", StringComparison.Ordinal) < body.IndexOf("\nENTRYPOINT", StringComparison.Ordinal), $"{stage}: USER must come before ENTRYPOINT");
            Assert.Contains("/app/certs/rds-ca.pem", body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Deployed_database_connections_verify_the_server_against_the_shipped_authority()
    {
        var secrets = File.ReadAllText(Path.Combine(Root, "deploy", "terraform", "environment", "secrets.tf"));
        var bundle = File.ReadAllText(Path.Combine(Root, "deploy", "certs", "rds-ca-ap-south-1.crt"));
        var provisioning = File.ReadAllText(Path.Combine(Root, "deploy", "terraform", "environment", "provisioning.tf"));

        Assert.Contains("SSL Mode=VerifyFull;Root Certificate=/app/certs/rds-ca.pem", secrets, StringComparison.Ordinal);
        // The provisioning task connects as the RDS master user, so it verifies the server too, with the image's bundle.
        Assert.Contains("{ name = \"Provisioning__Database__SslMode\", value = \"VerifyFull\" }", provisioning, StringComparison.Ordinal);
        Assert.Contains("{ name = \"Provisioning__Database__RootCertificate\", value = \"/app/certs/rds-ca.pem\" }", provisioning, StringComparison.Ordinal);
        Assert.Contains("image                  = \"${data.aws_ecr_repository.image[\"migrator\"].repository_url}:${var.image_tag}\"", provisioning, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Count(bundle, "-----BEGIN CERTIFICATE-----"));
        Assert.DoesNotContain("PRIVATE KEY", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void Base_images_are_pinned_by_digest_and_development_settings_never_ship()
    {
        var dockerfile = File.ReadAllText(Path.Combine(Root, "deploy", "docker", "Dockerfile")).ReplaceLineEndings("\n");

        Assert.All(BaseImageArgument().Matches(dockerfile), image => Assert.Matches("@sha256:[0-9a-f]{64}$", image.Groups["image"].Value));
        Assert.Contains("rm -f /out/*/appsettings.Development.json", dockerfile, StringComparison.Ordinal);
    }

    // S3g: the deployed Keycloak is the Compose version, built ahead with both production realms, and runs read-only with
    // its database connection verified against the shipped RDS authority.
    [Fact]
    public void The_keycloak_image_matches_compose_ships_both_realms_and_runs_read_only()
    {
        var dockerfile = File.ReadAllText(Path.Combine(Root, "deploy", "keycloak", "Dockerfile")).ReplaceLineEndings("\n");
        var compose = File.ReadAllText(Path.Combine(Root, "deploy", "compose", "compose.yaml"));
        var services = File.ReadAllText(Path.Combine(Root, "deploy", "terraform", "environment", "services.tf"));

        var image = BaseImageArgument().Match(dockerfile).Groups["image"].Value;
        Assert.Matches("@sha256:[0-9a-f]{64}$", image);
        Assert.Contains($"image: {image}", compose, StringComparison.Ordinal);
        Assert.Contains("deploy/identity/realms/production/tenantflow-realm.json /opt/keycloak/data/import/", dockerfile, StringComparison.Ordinal);
        Assert.Contains("deploy/identity/realms/production/tenantflow-platform-realm.json /opt/keycloak/data/import/", dockerfile, StringComparison.Ordinal);
        Assert.Contains("RUN /opt/keycloak/bin/kc.sh build", dockerfile, StringComparison.Ordinal);

        var keycloak = services[services.IndexOf("resource \"aws_ecs_task_definition\" \"keycloak\"", StringComparison.Ordinal)..];
        keycloak = keycloak[..keycloak.IndexOf("\n}\n", StringComparison.Ordinal)];
        Assert.Contains("command                = [\"start\", \"--optimized\", \"--import-realm\"]", keycloak, StringComparison.Ordinal);
        Assert.Contains("readonlyRootFilesystem = true", keycloak, StringComparison.Ordinal);
        Assert.Contains("sslmode=verify-full&sslrootcert=/opt/keycloak/certs/rds-ca.pem", keycloak, StringComparison.Ordinal);
    }

    // CI runs the database tests on a Testcontainers PostgreSQL (ADR-0019); it must be the image developers run in Compose.
    [Fact]
    public void The_testcontainers_postgres_image_matches_compose()
    {
        var fixture = File.ReadAllText(Path.Combine(Root, "tests", "Database", "TenantFlow.Database.Tests", "Support", "PostgreSqlContainerFixture.cs"));
        var compose = File.ReadAllText(Path.Combine(Root, "deploy", "compose", "compose.yaml"));

        var image = FixtureImageConstant().Match(fixture).Groups["image"].Value;
        Assert.Matches("^postgres:[^:@/]+@sha256:[0-9a-f]{64}$", image);
        Assert.Contains($"image: {image}", compose, StringComparison.Ordinal);
    }

    // The hosts refuse the simulator without its explicit switch, so a deployment that chooses it must set the switch.
    [Fact]
    public void A_deployment_that_chooses_the_payment_simulator_switches_it_on()
    {
        var services = File.ReadAllText(Path.Combine(Root, "deploy", "terraform", "environment", "services.tf"));

        Assert.Contains("var.payment_provider != \"simulator\" ? [] : [{ name = \"Payments__Simulator__Enabled\", value = \"true\" }]", services, StringComparison.Ordinal);
    }

    // SPEC-0012 criterion 17 (ADR-0024 amendment 1): separate Fargate tasks share no disk, so both hosts keep files in the
    // environment's bucket, with credentials from the task role only; the Migrator never touches files.
    [Fact]
    public void Deployed_api_and_worker_keep_files_in_the_environments_bucket_without_a_storage_credential()
    {
        var services = File.ReadAllText(Path.Combine(Root, "deploy", "terraform", "environment", "services.tf")).ReplaceLineEndings("\n");
        var shared = services[services.IndexOf("application_environment = concat(", StringComparison.Ordinal)..services.IndexOf("application_secrets = concat(", StringComparison.Ordinal)];

        Assert.Contains("{ name = \"Storage__Provider\", value = \"s3\" }", shared, StringComparison.Ordinal);
        Assert.Contains("{ name = \"Storage__S3__Bucket\", value = aws_s3_bucket.files.bucket }", shared, StringComparison.Ordinal);
        Assert.Contains("{ name = \"Storage__S3__Region\", value = var.aws_region }", shared, StringComparison.Ordinal);
        Assert.Contains("environment = concat(local.application_environment, [", TaskDefinition(services, "api"), StringComparison.Ordinal);
        Assert.Contains("environment            = concat(local.application_environment, local.worker_broker_environment)", TaskDefinition(services, "worker"), StringComparison.Ordinal);
        Assert.DoesNotContain("Storage__", TaskDefinition(services, "migrator"), StringComparison.Ordinal);
        Assert.DoesNotContain("Storage__Filesystem", services, StringComparison.Ordinal);
        Assert.DoesNotContain("AWS_ACCESS_KEY", services, StringComparison.Ordinal);
        Assert.DoesNotContain("AWS_SECRET", services, StringComparison.Ordinal);
    }

    // The text of one task definition resource in services.tf, up to the next resource.
    private static string TaskDefinition(string services, string name)
    {
        var start = services.IndexOf($"resource \"aws_ecs_task_definition\" \"{name}\" {{", StringComparison.Ordinal);
        Assert.True(start >= 0, $"no task definition named {name}");
        var next = services.IndexOf("\nresource ", start + 1, StringComparison.Ordinal);
        return services[start..(next < 0 ? services.Length : next)];
    }

    // The text of one stage: from its FROM line to the next FROM or the end of the file.
    private static string StageBody(string dockerfile, string stage)
    {
        var start = dockerfile.IndexOf($" AS {stage}\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"no stage named {stage}");
        var next = dockerfile.IndexOf("\nFROM ", start, StringComparison.Ordinal);
        return dockerfile[start..(next < 0 ? dockerfile.Length : next + 1)];
    }

    // The ARG a stage's FROM line names, for example ASPNET_IMAGE in "FROM ${ASPNET_IMAGE} AS api".
    private static string BaseImageOf(string dockerfile, string stage)
    {
        var match = Regex.Match(dockerfile, $@"^FROM \$\{{(?<name>\w+)\}} AS {stage}$", RegexOptions.Multiline);
        Assert.True(match.Success, $"{stage}: FROM must name a base-image argument");
        return match.Groups["name"].Value;
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

    [GeneratedRegex(@"^ARG (?<name>\w+_IMAGE)=(?<image>\S+)$", RegexOptions.Multiline)]
    private static partial Regex BaseImageArgument();

    [GeneratedRegex(@"private const string Image = ""(?<image>[^""]+)"";")]
    private static partial Regex FixtureImageConstant();
}
