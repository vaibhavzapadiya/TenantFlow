using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using TenantFlow.Migrator.Provisioning;

namespace TenantFlow.Database.Tests.Provisioning;

/// <summary>
/// <c>TenantFlow.Migrator provision</c> (ADR-0011, ADR-0012, ADR-0035, MW-A02) against a throwaway PostgreSQL and
/// RabbitMQ, never the shared Compose stack (roles and broker users are server-wide): least-privilege roles and
/// Keycloak's separate database, password rotation, the broker's application user, and settings errors that name only
/// their keys. Each check runs provisioning twice, since it must be repeatable.
/// </summary>
public sealed class ProvisioningTests(ProvisioningTests.Containers containers) : IClassFixture<ProvisioningTests.Containers>
{
    private const string Database = "tenantflow";
    private const string VirtualHost = "tenantflow";
    private const string SentinelValue = "sentinel-value-that-must-never-appear-in-logs";

    [Fact]
    public async Task Roles_get_only_login_and_their_database_rights_and_keycloak_gets_its_own_database()
    {
        var settings = DatabaseSettings(NewPassword(), NewPassword(), keycloakPassword: NewPassword());

        var runs = new[] { await RunAsync(settings), await RunAsync(settings) };

        Assert.All(runs, run => Assert.Equal(0, run.ExitCode));
        Assert.True(await AdminScalarAsync<bool>(
            "SELECT count(*) = 3 AND bool_and(rolcanlogin AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication) " +
            "FROM pg_roles WHERE rolname IN ('tenantflow_owner', 'tenantflow_app', 'keycloak')"));
        Assert.True(await AdminScalarAsync<bool>(
            "SELECT has_database_privilege('tenantflow_owner', 'tenantflow', 'CREATE') " +
            "AND has_database_privilege('tenantflow_app', 'tenantflow', 'CONNECT') AND NOT has_database_privilege('tenantflow_app', 'tenantflow', 'CREATE') " +
            "AND NOT has_database_privilege('keycloak', 'tenantflow', 'CONNECT') AND NOT has_database_privilege('tenantflow_app', 'keycloak', 'CONNECT') " +
            "AND (SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = 'keycloak') = 'keycloak'"));
    }

    [Fact]
    public async Task A_changed_password_is_applied_on_the_next_run()
    {
        var (first, second) = (NewPassword(), NewPassword());

        Assert.Equal(0, (await RunAsync(DatabaseSettings(NewPassword(), first))).ExitCode);
        Assert.Equal(0, (await RunAsync(DatabaseSettings(NewPassword(), second))).ExitCode);

        Assert.Equal("tenantflow_app", await ScalarAsync<string>(AppConnection(second), "SELECT current_user"));
        var refused = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync<string>(AppConnection(first), "SELECT current_user"));
        Assert.Equal(PostgresErrorCodes.InvalidPassword, refused.SqlState);
    }

    [Fact]
    public async Task The_broker_user_has_no_tags_rights_only_in_its_virtual_host_and_no_management_access()
    {
        var (user, password) = ($"app-{Guid.NewGuid():N}", NewPassword());
        var settings = new Dictionary<string, string>(DatabaseSettings(NewPassword(), NewPassword()))
        {
            ["Provisioning:Broker:ManagementUrl"] = $"http://{containers.RabbitMq.Address}",
            ["Provisioning:Broker:OperatorUsername"] = Containers.BrokerOperator,
            ["Provisioning:Broker:OperatorPassword"] = containers.BrokerOperatorPassword,
            ["Provisioning:Broker:AppUsername"] = user,
            ["Provisioning:Broker:AppPassword"] = password,
            ["Provisioning:Broker:VirtualHost"] = VirtualHost,
        };

        var runs = new[] { await RunAsync(settings), await RunAsync(settings) };

        Assert.All(runs, run => Assert.Equal(0, run.ExitCode));
        using var client = containers.BrokerClient(Containers.BrokerOperator, containers.BrokerOperatorPassword);
        var tags = (await client.GetFromJsonAsync<JsonElement>($"api/users/{user}", TestContext.Current.CancellationToken)).GetProperty("tags");
        Assert.True(tags.ValueKind == JsonValueKind.Array ? tags.GetArrayLength() == 0 : tags.GetString() == string.Empty, $"tags: {tags}");
        var permissions = await client.GetFromJsonAsync<JsonElement[]>($"api/users/{user}/permissions", TestContext.Current.CancellationToken);
        var only = Assert.Single(permissions!);
        Assert.Equal((VirtualHost, ".*", ".*", ".*"), (only.GetProperty("vhost").GetString(), only.GetProperty("configure").GetString(),
            only.GetProperty("write").GetString(), only.GetProperty("read").GetString()));
        using var asApp = containers.BrokerClient(user, password);
        using var whoami = await asApp.GetAsync("api/whoami", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, whoami.StatusCode);
    }

    [Fact]
    public async Task Missing_settings_stop_provisioning_and_name_only_the_keys()
    {
        var settings = new Dictionary<string, string>(DatabaseSettings(SentinelValue, string.Empty))
        {
            ["Provisioning:Database:Host"] = string.Empty,
            ["Provisioning:Broker:ManagementUrl"] = $"http://{containers.RabbitMq.Address}",
        };

        var (exitCode, logs) = await RunAsync(settings);

        var text = string.Join('\n', logs);
        Assert.Equal(1, exitCode);
        Assert.Contains("Provisioning:Database:Host", text, StringComparison.Ordinal);
        Assert.Contains("Provisioning:Database:AppPassword", text, StringComparison.Ordinal);
        Assert.Contains("Provisioning:Broker:OperatorPassword", text, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelValue, text, StringComparison.Ordinal);
    }

    private Dictionary<string, string> DatabaseSettings(string ownerPassword, string appPassword, string? keycloakPassword = null)
    {
        var settings = new Dictionary<string, string>
        {
            ["Provisioning:Database:Host"] = containers.Postgres.Host,
            ["Provisioning:Database:Port"] = containers.Postgres.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Provisioning:Database:Database"] = Database,
            ["Provisioning:Database:Username"] = Containers.Administrator,
            ["Provisioning:Database:Password"] = containers.AdministratorPassword,
            // The throwaway server has no certificate; deployed environments use VerifyFull with the RDS bundle.
            ["Provisioning:Database:SslMode"] = "Disable",
            ["Provisioning:Database:OwnerPassword"] = ownerPassword,
            ["Provisioning:Database:AppPassword"] = appPassword,
            // Always passed, so an ambient Provisioning__Broker__ManagementUrl cannot turn the broker step on by accident.
            ["Provisioning:Broker:ManagementUrl"] = string.Empty,
        };
        if (keycloakPassword is not null)
        {
            settings["Provisioning:Database:KeycloakPassword"] = keycloakPassword;
        }

        return settings;
    }

    private static async Task<(int ExitCode, IReadOnlyList<string> Logs)> RunAsync(IReadOnlyDictionary<string, string> settings)
    {
        var capture = new CapturingLoggerProvider();
        var exitCode = await ProvisioningApplication.RunAsync(
            ["--environment=Production", .. settings.Select(static s => $"--{s.Key}={s.Value}")],
            logging => logging.AddProvider(capture),
            TestContext.Current.CancellationToken);
        return (exitCode, capture.Messages);
    }

    private string AppConnection(string password) =>
        $"Host={containers.Postgres.Host};Port={containers.Postgres.Port};Database={Database};Username=tenantflow_app;Password={password};Pooling=false";

    private Task<T> AdminScalarAsync<T>(string sql) => ScalarAsync<T>(containers.AdministratorConnection, sql);

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static string NewPassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>A throwaway PostgreSQL and RabbitMQ, the images Compose runs, for this class only.</summary>
    public sealed class Containers : IAsyncLifetime
    {
        public const string Administrator = "postgres";
        public const string BrokerOperator = "operator";

        // The images and digests of deploy/compose/compose.yaml.
        private const string PostgresImage = "postgres:17.11@sha256:b8faeff7712663c2773a190181e1246e37e364b887611c04658c28b44e2408fe";
        private const string RabbitMqImage = "rabbitmq:4.3.6-management-alpine@sha256:3505b6096cdbc083bcc363e74366f575ecd0c1a55be4dadcbc60fa08e34647e1";

        public ThrowawayContainer Postgres { get; private set; } = null!;

        public ThrowawayContainer RabbitMq { get; private set; } = null!;

        // Exist only for this class's containers and are never written anywhere (AGENTS.md rule 6).
        public string AdministratorPassword { get; } = NewPassword();

        public string BrokerOperatorPassword { get; } = NewPassword();

        public string AdministratorConnection =>
            $"Host={Postgres.Host};Port={Postgres.Port};Database={Database};Username={Administrator};Password={AdministratorPassword};Pooling=false";

        public HttpClient BrokerClient(string username, string password)
        {
            var client = new HttpClient { BaseAddress = new Uri($"http://{RabbitMq.Address}/") };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
            return client;
        }

        public async ValueTask InitializeAsync()
        {
            var ct = TestContext.Current.CancellationToken;
            Postgres = await ThrowawayContainer.StartAsync(PostgresImage, 5432,
                new Dictionary<string, string> { ["POSTGRES_PASSWORD"] = AdministratorPassword, ["POSTGRES_DB"] = Database }, ct);
            RabbitMq = await ThrowawayContainer.StartAsync(RabbitMqImage, 15672,
                new Dictionary<string, string> { ["RABBITMQ_DEFAULT_USER"] = BrokerOperator, ["RABBITMQ_DEFAULT_PASS"] = BrokerOperatorPassword }, ct);

            await ThrowawayContainer.WaitUntilAsync(() => ScalarAsync<int>(AdministratorConnection, "SELECT 1"), ct);
            await ThrowawayContainer.WaitUntilAsync(async () =>
            {
                using var client = BrokerClient(BrokerOperator, BrokerOperatorPassword);
                using var overview = await client.GetAsync("api/overview", ct);
                overview.EnsureSuccessStatusCode();
            }, ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (Postgres is not null)
            {
                await Postgres.DisposeAsync();
            }

            if (RabbitMq is not null)
            {
                await RabbitMq.DisposeAsync();
            }
        }
    }
}
