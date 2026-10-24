using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace TenantFlow.Database.Tests.Tenants;

/// <summary>
/// SPEC-0002 criteria 24 (create part) and 25 against real PostgreSQL: platform operators create,
/// suspend, and reactivate tenants; invalid input and illegal transitions are rejected and write
/// nothing; every change is audited once; concurrent changes have one winner.
/// </summary>
public sealed class TenantLifecycleTests : IAsyncLifetime
{
    private TestDatabase? _database;
    private WebApplicationFactory<Program>? _factory;

    private static readonly string[] LoserCodes = ["tenant.already_suspended", "tenant.concurrent_update"];

    private TestDatabase Database => _database ?? throw new InvalidOperationException("Not initialized.");

    public async ValueTask InitializeAsync()
    {
        var database = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var (exitCode, logs) = await MigratorRun.RunAsync(database.OwnerConnectionString);
        if (exitCode != 0)
        {
            await database.DisposeAsync();
            Assert.Fail(string.Join(Environment.NewLine, logs));
        }

        _database = database;
        _factory = ApiFactory.CreateProduction(connectionString: database.AppConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Fact]
    public async Task Operator_creates_an_active_tenant_and_the_change_is_audited()
    {
        using var client = Client(TestIssuer.Platform.CreateOperatorToken("operator-a"));

        using var created = await client.PostAsJsonAsync("/v1/platform/tenants", new { name = "  Acme Ltd  " }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var tenant = await ReadTenantAsync(created);
        Assert.Equal("Acme Ltd", tenant.Name);
        Assert.Equal("active", tenant.Status);
        Assert.Equal($"/v1/platform/tenants/{tenant.Id}", created.Headers.Location?.ToString());
        using var read = await client.GetAsync(created.Headers.Location, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(["tenant.created|operator-a|" + TestIssuer.PlatformIssuer + "||active"], await AuditAsync(tenant.Id));
    }

    [Theory]
    [InlineData("""{"name":""}""")]
    [InlineData("""{"name":"   "}""")]
    [InlineData("""{}""")]
    [InlineData(null)]
    public async Task Invalid_name_is_rejected_with_400_and_writes_nothing(string? body)
    {
        using var client = Client(TestIssuer.Platform.CreateOperatorToken());
        body ??= $$"""{"name":"{{new string('x', 201)}}"}""";

        using var response = await client.PostAsync(
            "/v1/platform/tenants", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("request.validation_failed", problem.RootElement.GetProperty("code").GetString());
        var error = Assert.Single(problem.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal("name", error.GetProperty("field").GetString());
        Assert.StartsWith("tenant.name.", error.GetProperty("code").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM tenants.tenants"));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM tenants.tenant_audit"));
    }

    [Fact]
    public async Task Platform_token_without_the_operator_role_creates_nothing()
    {
        using var client = Client(TestIssuer.Platform.CreateToken("not-an-operator"));

        using var response = await client.PostAsJsonAsync("/v1/platform/tenants", new { name = "Acme" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM tenants.tenants"));
    }

    [Fact]
    public async Task Suspend_and_reactivate_follow_the_lifecycle_and_each_change_is_audited_once()
    {
        using var client = Client(TestIssuer.Platform.CreateOperatorToken("operator-a"));
        var tenant = await CreateAsync(client, "Acme");

        using var suspended = await client.PostAsync($"/v1/platform/tenants/{tenant.Id}/suspend", null, TestContext.Current.CancellationToken);
        using var suspendedAgain = await client.PostAsync($"/v1/platform/tenants/{tenant.Id}/suspend", null, TestContext.Current.CancellationToken);
        using var reactivated = await client.PostAsync($"/v1/platform/tenants/{tenant.Id}/reactivate", null, TestContext.Current.CancellationToken);
        using var reactivatedAgain = await client.PostAsync($"/v1/platform/tenants/{tenant.Id}/reactivate", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
        Assert.Equal("suspended", (await ReadTenantAsync(suspended)).Status);
        Assert.Equal(HttpStatusCode.Conflict, suspendedAgain.StatusCode);
        Assert.Equal("application/problem+json", suspendedAgain.Content.Headers.ContentType?.MediaType);
        Assert.Equal("tenant.already_suspended", await ReadCodeAsync(suspendedAgain));
        Assert.Equal(HttpStatusCode.OK, reactivated.StatusCode);
        Assert.Equal("active", (await ReadTenantAsync(reactivated)).Status);
        Assert.Equal(HttpStatusCode.Conflict, reactivatedAgain.StatusCode);
        Assert.Equal("tenant.not_suspended", await ReadCodeAsync(reactivatedAgain));
        var issuer = TestIssuer.PlatformIssuer;
        Assert.Equal(
            [$"tenant.created|operator-a|{issuer}||active", $"tenant.suspended|operator-a|{issuer}|active|suspended", $"tenant.reactivated|operator-a|{issuer}|suspended|active"],
            await AuditAsync(tenant.Id));
    }

    [Theory]
    [InlineData("GET", "")]
    [InlineData("POST", "/suspend")]
    [InlineData("POST", "/reactivate")]
    public async Task Unknown_tenant_returns_404(string method, string suffix)
    {
        using var client = Client(TestIssuer.Platform.CreateOperatorToken());

        using var request = new HttpRequestMessage(new HttpMethod(method), $"/v1/platform/tenants/{Guid.NewGuid()}{suffix}");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("tenant.not_found", await ReadCodeAsync(response));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM tenants.tenant_audit"));
    }

    [Fact]
    public async Task Concurrent_suspensions_have_exactly_one_winner()
    {
        using var first = Client(TestIssuer.Platform.CreateOperatorToken("operator-a"));
        using var second = Client(TestIssuer.Platform.CreateOperatorToken("operator-b"));
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var tenant = await CreateAsync(first, $"Acme {attempt}");

            var responses = await Task.WhenAll(
                first.PostAsync($"/v1/platform/tenants/{tenant.Id}/suspend", null, TestContext.Current.CancellationToken),
                second.PostAsync($"/v1/platform/tenants/{tenant.Id}/suspend", null, TestContext.Current.CancellationToken));

            var statuses = responses.Select(static response => response.StatusCode).Order().ToArray();
            var loser = responses.Single(static response => response.StatusCode == HttpStatusCode.Conflict);
            var loserCode = await ReadCodeAsync(loser);
            foreach (var response in responses)
            {
                response.Dispose();
            }

            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], statuses);
            // Either the second request read the committed suspension, or it raced and lost the row version.
            Assert.Contains(loserCode, LoserCodes);
            Assert.Equal(1, await CountAsync($"SELECT count(*) FROM tenants.tenant_audit WHERE tenant_id = '{tenant.Id}' AND action = 'tenant.suspended'"));
        }
    }

    [Fact]
    public async Task Rejected_input_and_illegal_transitions_are_not_logged_as_errors()
    {
        // Expected outcomes must not look like incidents in the logs (blueprint section 16).
        var logs = new CapturingLoggerProvider();
        await using var factory = ApiFactory.CreateProduction(
            services => services.AddSingleton<ILoggerProvider>(logs),
            connectionString: Database.AppConnectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Platform.CreateOperatorToken());
        var tenant = await CreateAsync(client, "Acme");

        using var invalid = await client.PostAsJsonAsync("/v1/platform/tenants", new { name = "" }, TestContext.Current.CancellationToken);
        using var suspended = await client.PostAsync($"/v1/platform/tenants/{tenant.Id}/suspend", null, TestContext.Current.CancellationToken);
        using var conflict = await client.PostAsync($"/v1/platform/tenants/{tenant.Id}/suspend", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.DoesNotContain(logs.Messages, static line => line.StartsWith("Error:", StringComparison.Ordinal) || line.StartsWith("Critical:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_stored_status_nobody_recognises_fails_loudly_instead_of_being_read_as_another_status()
    {
        // The check constraint normally stops this; it is dropped here to simulate corrupted or hand-edited data.
        using var client = Client(TestIssuer.Platform.CreateOperatorToken());
        var tenant = await CreateAsync(client, "Acme");
        await using (var owner = new NpgsqlConnection(Database.OwnerConnectionString))
        {
            await owner.OpenAsync(TestContext.Current.CancellationToken);
            await TestDatabase.ExecuteAsync(owner, "ALTER TABLE tenants.tenants DROP CONSTRAINT ck_tenants_status", TestContext.Current.CancellationToken);
            await TestDatabase.ExecuteAsync(owner, $"UPDATE tenants.tenants SET status = 'deleted' WHERE id = '{tenant.Id}'", TestContext.Current.CancellationToken);
        }

        using var response = await client.GetAsync($"/v1/platform/tenants/{tenant.Id}", TestContext.Current.CancellationToken);

        // A 500, not a 200 that quietly says "suspended".
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("server.unexpected", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Production_logs_hold_no_sql_and_no_tenant_names()
    {
        // Blueprint section 13: logs are not a secondary database. With the shipped logging levels, real traffic
        // through the real database must not write the SQL it ran, or what the tenant is called.
        var logs = new CapturingLoggerProvider();
        await using var factory = ApiFactory.CreateProduction(
            services => services.AddSingleton<ILoggerProvider>(logs),
            connectionString: Database.AppConnectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Platform.CreateOperatorToken());

        var tenant = await CreateAsync(client, "Distinctive Corp Name");
        using var suspended = await client.PostAsync($"/v1/platform/tenants/{tenant.Id}/suspend", null, TestContext.Current.CancellationToken);
        using var read = await client.GetAsync($"/v1/platform/tenants/{tenant.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        foreach (var forbidden in new[] { "Executed DbCommand", "INSERT INTO", "SELECT ", "tenants.tenants", "Distinctive" })
        {
            Assert.DoesNotContain(logs.Messages, line => line.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [InlineData("Acme Ltd")]
    [InlineData("acme ltd")]
    [InlineData("ACME LTD")]
    [InlineData("   Acme Ltd   ")]
    public async Task A_name_equal_ignoring_case_and_surrounding_whitespace_is_409_name_taken_and_writes_nothing(string duplicate)
    {
        using var client = Client(TestIssuer.Platform.CreateOperatorToken("operator-a"));
        var first = await CreateAsync(client, "Acme Ltd");

        using var response = await client.PostAsJsonAsync("/v1/platform/tenants", new { name = duplicate }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("tenant.name_taken", await ReadCodeAsync(response));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM tenants.tenants"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM tenants.tenant_audit"));
        Assert.Single(await AuditAsync(first.Id));
    }

    [Fact]
    public async Task A_duplicate_name_is_an_expected_outcome_and_is_not_logged_as_an_error()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = ApiFactory.CreateProduction(
            services => services.AddSingleton<ILoggerProvider>(logs),
            connectionString: Database.AppConnectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Platform.CreateOperatorToken());
        _ = await CreateAsync(client, "Acme Ltd");

        using var duplicate = await client.PostAsJsonAsync("/v1/platform/tenants", new { name = "ACME LTD" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.DoesNotContain(logs.Messages, static line => line.StartsWith("Error:", StringComparison.Ordinal) || line.StartsWith("Critical:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_different_name_is_still_created_and_the_conflict_does_not_name_the_other_tenant()
    {
        using var client = Client(TestIssuer.Platform.CreateOperatorToken());
        var first = await CreateAsync(client, "Acme Ltd");

        var second = await CreateAsync(client, "Acme Ltd 2");
        using var duplicate = await client.PostAsJsonAsync("/v1/platform/tenants", new { name = "ACME LTD" }, TestContext.Current.CancellationToken);

        Assert.NotEqual(first.Id, second.Id);
        var body = await duplicate.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(first.Id.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Acme", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wildcard_characters_in_a_name_are_ordinary_characters_in_the_duplicate_check()
    {
        using var client = Client(TestIssuer.Platform.CreateOperatorToken());
        _ = await CreateAsync(client, "AcmeXLtd");
        _ = await CreateAsync(client, "100 and more Acme");

        // "_" and "%" in a new name must not match other characters in stored names: these are new tenants.
        _ = await CreateAsync(client, "Acme_Ltd");
        _ = await CreateAsync(client, "100% Acme");
        using var real = await client.PostAsJsonAsync("/v1/platform/tenants", new { name = "ACME_LTD" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, real.StatusCode);
        Assert.Equal(4, await CountAsync("SELECT count(*) FROM tenants.tenants"));
    }

    [Fact]
    public async Task A_suspended_tenant_keeps_its_name()
    {
        using var client = Client(TestIssuer.Platform.CreateOperatorToken());
        var tenant = await CreateAsync(client, "Acme Ltd");
        using var suspended = await client.PostAsync($"/v1/platform/tenants/{tenant.Id}/suspend", null, TestContext.Current.CancellationToken);

        using var again = await client.PostAsJsonAsync("/v1/platform/tenants", new { name = "acme ltd" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("tenant.name_taken", await ReadCodeAsync(again));
    }

    [Fact]
    public async Task Creating_the_same_name_at_once_has_exactly_one_winner_every_time()
    {
        using var first = Client(TestIssuer.Platform.CreateOperatorToken("operator-a"));
        using var second = Client(TestIssuer.Platform.CreateOperatorToken("operator-b"));
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var name = $"Race {attempt}";

            var responses = await Task.WhenAll(
                first.PostAsJsonAsync("/v1/platform/tenants", new { name }, TestContext.Current.CancellationToken),
                second.PostAsJsonAsync("/v1/platform/tenants", new { name = name.ToUpperInvariant() }, TestContext.Current.CancellationToken));

            var statuses = responses.Select(static response => response.StatusCode).Order().ToArray();
            var loser = responses.Single(static response => response.StatusCode == HttpStatusCode.Conflict);
            var loserCode = await ReadCodeAsync(loser);
            foreach (var response in responses)
            {
                response.Dispose();
            }

            Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], statuses);
            Assert.Equal("tenant.name_taken", loserCode);
            Assert.Equal(1, await CountAsync($"SELECT count(*) FROM tenants.tenants WHERE lower(name) = '{name.ToLowerInvariant()}'"));
            Assert.Equal(1, await CountAsync($"SELECT count(*) FROM tenants.tenant_audit a JOIN tenants.tenants t ON t.id = a.tenant_id WHERE lower(t.name) = '{name.ToLowerInvariant()}'"));
        }
    }

    [Fact]
    public async Task The_database_itself_refuses_a_duplicate_name_so_a_code_bug_cannot_create_one()
    {
        await using var connection = new NpgsqlConnection(Database.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await TestDatabase.ExecuteAsync(connection, InsertTenant("Direct Name"), TestContext.Current.CancellationToken);

        var duplicate = await Assert.ThrowsAsync<PostgresException>(
            () => TestDatabase.ExecuteAsync(connection, InsertTenant("DIRECT name"), TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal("ux_tenants_name_lower", duplicate.ConstraintName);

        static string InsertTenant(string name) =>
            $"INSERT INTO tenants.tenants (id, name, status, created_at, updated_at) VALUES ('{Guid.CreateVersion7()}', '{name}', 'active', now(), now())";
    }

    [Theory]
    [InlineData("UPDATE tenants.tenant_audit SET action = 'x'")]
    [InlineData("DELETE FROM tenants.tenant_audit")]
    public async Task Application_role_cannot_change_tenant_audit_records(string sql)
    {
        await using var connection = new NpgsqlConnection(Database.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => TestDatabase.ExecuteAsync(connection, sql, TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    private HttpClient Client(string token)
    {
        var client = (_factory ?? throw new InvalidOperationException("Not initialized.")).CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<TenantBody> CreateAsync(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync("/v1/platform/tenants", new { name }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadTenantAsync(response);
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .RootElement.GetProperty("code").GetString();

    private static async Task<TenantBody> ReadTenantAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<TenantBody>(TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("Empty tenant body.");

    private async Task<long> CountAsync(string sql) =>
        (await MigratorRun.QueryAsync<long>(Database.OwnerConnectionString, sql))[0];

    // action|subject|issuer|old|new, oldest first.
    private async Task<IReadOnlyList<string>> AuditAsync(Guid tenantId) =>
        await MigratorRun.QueryAsync<string>(
            Database.OwnerConnectionString,
            """
            SELECT action || '|' || actor_subject || '|' || actor_issuer || '|' || coalesce(old_status, '') || '|' || new_status
            FROM tenants.tenant_audit WHERE tenant_id = @tenant ORDER BY id
            """,
            ("tenant", tenantId));

    private sealed record TenantBody(Guid Id, string Name, string Status);
}
