namespace TenantFlow.Database.Tests.Support;

/// <summary>
/// SPEC-0002 criterion 4: without the local PostgreSQL, database tests fail with instructions, never
/// a silent skip. (That no test database remains after a run is checked by <see cref="PostgreSqlContainerFixture"/>.)
/// </summary>
public sealed class DatabaseTestSettingsTests
{
    [Fact]
    public void Missing_settings_fail_with_the_commands_that_run_the_tests()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => DatabaseTestSettings.From(static _ => null));

        Assert.Contains("docker compose up -d --wait", exception.Message, StringComparison.Ordinal);
        Assert.Contains("dotnet run --project tests/Database/TenantFlow.Database.Tests", exception.Message, StringComparison.Ordinal);
        Assert.Contains(DatabaseTestSettings.AdminVariable, exception.Message, StringComparison.Ordinal);
    }

    // ADR-0037: with no environment variables the tests find the running Compose stack, at Compose's own defaults.
    [Fact]
    public void Local_settings_are_the_compose_defaults()
    {
        var app = new Npgsql.NpgsqlConnectionStringBuilder(LocalStack.AppConnectionString);

        Assert.Equal("127.0.0.1", app.Host);
        Assert.Equal(ComposeSettings.GetPort("POSTGRES_PORT"), app.Port);
        Assert.Equal(TestDatabase.AppRole, app.Username);
        Assert.Null(app.Database);
        Assert.Equal("postgres", new Npgsql.NpgsqlConnectionStringBuilder(LocalStack.AdminConnectionString).Database);
        Assert.Equal(ComposeSettings.Get("RABBITMQ_USER"), LocalStack.RabbitMqUser);
    }

    [Fact]
    public async Task Unreachable_server_fails_with_the_command_that_starts_it()
    {
        // A closed local port refuses the connection immediately. The password is a placeholder.
        const string Unreachable = "Host=127.0.0.1;Port=1;Username=tenantflow;Password=placeholder;Timeout=2";
        var settings = DatabaseTestSettings.From(name => name == DatabaseTestSettings.RunIdVariable ? "unreachable" : Unreachable);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TestDatabase.CreateAsync(settings, TestContext.Current.CancellationToken));

        Assert.Contains("docker compose up -d", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("UPPER")]
    [InlineData("a\"; DROP DATABASE x; --")]
    [InlineData("fifteen-chars-x")]
    public void Run_id_must_be_a_safe_identifier_fragment(string runId)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => DatabaseTestSettings.From(name => name == DatabaseTestSettings.RunIdVariable ? runId : "Host=127.0.0.1"));

        Assert.Contains(DatabaseTestSettings.RunIdVariable, exception.Message, StringComparison.Ordinal);
    }
}
