using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TenantFlow.Api.Tests.ErrorHandling;

/// <summary>
/// The error contract (ADR-0016; pre-S5 review 2.2, 2.3, 2.5): every failure is RFC 9457 Problem Details
/// with a stable <c>code</c>, a <c>traceId</c> that matches the request span, and nothing internal.
/// Codes and status values here are literals on purpose: they are the public contract.
/// </summary>
public sealed class ErrorContractTests
{
    private const string Create = "/v1/platform/tenants";

    [Fact]
    public async Task Validation_failure_is_400_with_a_code_and_field_errors()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = Client(factory);

        using var response = await PostAsync(client, Create, """{"name":""}""");

        var problem = await ReadProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("request.validation_failed", problem.GetProperty("code").GetString());
        Assert.Equal("urn:tenantflow:problem:request.validation_failed", problem.GetProperty("type").GetString());
        Assert.Equal("One or more fields are invalid.", problem.GetProperty("title").GetString());
        var error = Assert.Single(problem.GetProperty("errors").EnumerateArray());
        Assert.Equal("name", error.GetProperty("field").GetString());
        Assert.Equal("tenant.name.required", error.GetProperty("code").GetString());
        Assert.Equal("Name is required.", error.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("""{}""", "tenant.name.required")]
    [InlineData("""{"name":null}""", "tenant.name.required")]
    [InlineData("""{"name":"   "}""", "tenant.name.required")]
    public async Task Missing_or_blank_name_reports_the_required_code(string body, string expectedCode)
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = Client(factory);

        using var response = await PostAsync(client, Create, body);

        var problem = await ReadProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(expectedCode, Assert.Single(problem.GetProperty("errors").EnumerateArray()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Too_long_name_reports_its_own_code()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = Client(factory);

        using var response = await PostAsync(client, Create, $$"""{"name":"{{new string('x', 201)}}"}""");

        var problem = await ReadProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("tenant.name.too_long", Assert.Single(problem.GetProperty("errors").EnumerateArray()).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("")]
    public async Task Malformed_or_empty_body_is_400_request_malformed_and_never_echoes_the_input(string body)
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = Client(factory);

        using var response = await PostAsync(client, Create, body);

        var problem = await ReadProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("request.malformed", problem.GetProperty("code").GetString());
        Assert.DoesNotContain("not json", problem.GetRawText(), StringComparison.Ordinal);
        Assert.False(problem.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task Malformed_body_gets_the_same_answer_in_Development_as_in_Production()
    {
        // Minimal APIs throw on bad requests only in Development unless told otherwise: without the explicit
        // setting this was a 500 in Development and an empty 400 in Production.
        await using var factory = ApiFactory.CreateDevelopment();
        using var client = Client(factory);

        using var response = await PostAsync(client, Create, "{not json");

        var problem = await ReadProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("request.malformed", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Bad_requests_take_the_same_code_path_in_every_environment()
    {
        // Both paths answer 400 request.malformed, so the response cannot tell them apart; the setting itself
        // is what keeps Production and Development on one path (minimal APIs default to throwing only in Development).
        await using var production = ApiFactory.CreateProduction();
        await using var development = ApiFactory.CreateDevelopment();
        using var productionClient = production.CreateClient();
        using var developmentClient = development.CreateClient();

        Assert.True(production.Services.GetRequiredService<IOptions<RouteHandlerOptions>>().Value.ThrowOnBadRequest);
        Assert.True(development.Services.GetRequiredService<IOptions<RouteHandlerOptions>>().Value.ThrowOnBadRequest);
    }

    [Fact]
    public async Task Wrong_content_type_is_415_even_for_a_signed_in_operator()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = Client(factory);

        using var response = await client.PostAsync(
            new Uri(Create, UriKind.Relative), new StringContent("name=x", Encoding.UTF8, "text/plain"), TestContext.Current.CancellationToken);

        var problem = await ReadProblemAsync(response, HttpStatusCode.UnsupportedMediaType);
        Assert.Equal("request.unsupported_media_type", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Wrong_method_is_405_with_an_Allow_header_even_for_a_signed_in_operator()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = Client(factory);

        using var response = await client.GetAsync(new Uri(Create, UriKind.Relative), TestContext.Current.CancellationToken);

        var problem = await ReadProblemAsync(response, HttpStatusCode.MethodNotAllowed);
        Assert.Equal("request.method_not_allowed", problem.GetProperty("code").GetString());
        Assert.Contains("POST", response.Content.Headers.Allow);
    }

    [Fact]
    public async Task Unknown_paths_stay_denied_by_the_fallback_policy()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = Client(factory);

        using var signedIn = await client.GetAsync(new Uri("/v1/nothing-here", UriKind.Relative), TestContext.Current.CancellationToken);

        // Operator tokens are not tenant tokens, so the deny-by-default fallback challenges them (SPEC-0002 criterion 8).
        var problem = await ReadProblemAsync(signedIn, HttpStatusCode.Unauthorized);
        Assert.Equal("authentication.required", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Missing_credentials_are_401_authentication_required()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/v1/me", UriKind.Relative), TestContext.Current.CancellationToken);

        var problem = await ReadProblemAsync(response, HttpStatusCode.Unauthorized);
        Assert.Equal("authentication.required", problem.GetProperty("code").GetString());
        Assert.Equal("urn:tenantflow:problem:authentication.required", problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Insufficient_permission_is_403_authorization_forbidden()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Platform.CreateToken("not-an-operator"));

        using var response = await client.GetAsync(new Uri($"{Create}/{Guid.NewGuid()}", UriKind.Relative), TestContext.Current.CancellationToken);

        var problem = await ReadProblemAsync(response, HttpStatusCode.Forbidden);
        Assert.Equal("authorization.forbidden", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unexpected_fault_is_a_generic_500_that_leaks_nothing_and_is_logged_as_an_error()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = ApiFactory.CreateProduction(services => services.AddSingleton<ILoggerProvider>(logs));
        using var client = Client(factory);

        // PostgreSQL is unreachable in this host (a closed local port), so the use case fails for real.
        using var response = await PostAsync(client, Create, """{"name":"Acme"}""");

        var problem = await ReadProblemAsync(response, HttpStatusCode.InternalServerError);
        Assert.Equal("server.unexpected", problem.GetProperty("code").GetString());
        Assert.Equal("An unexpected error occurred.", problem.GetProperty("title").GetString());
        var raw = problem.GetRawText();
        foreach (var internalDetail in new[] { "127.0.0.1", "Npgsql", "Exception", "postgres", "Timeout", " at " })
        {
            Assert.DoesNotContain(internalDetail, raw, StringComparison.OrdinalIgnoreCase);
        }

        // The fault itself is not hidden from operators: it is logged with its exception.
        Assert.Contains(logs.Messages, static line => line.StartsWith("Error:", StringComparison.Ordinal) && line.Contains("Npgsql", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Expected_failures_are_not_logged_as_errors()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = ApiFactory.CreateProduction(services => services.AddSingleton<ILoggerProvider>(logs));
        using var client = Client(factory);

        using var validation = await PostAsync(client, Create, """{"name":""}""");
        using var malformed = await PostAsync(client, Create, "{not json");

        Assert.Equal(HttpStatusCode.BadRequest, validation.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.DoesNotContain(logs.Messages, static line => line.StartsWith("Error:", StringComparison.Ordinal) || line.StartsWith("Critical:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/v1/me", 401)]
    [InlineData("/v1/platform/tenants", 400)]
    public async Task Trace_id_in_the_problem_is_the_trace_of_the_request(string path, int expectedStatus)
    {
        // The test registers its own listener before the host exists, so the request activity is recorded
        // deterministically. Waiting for OpenTelemetry's exported span instead was flaky in the test host
        // (T-07): the trace id was there, but the export sometimes never reached the capture processor.
        var traceIds = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Microsoft.AspNetCore",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                if (activity.OperationName == "Microsoft.AspNetCore.Hosting.HttpRequestIn")
                {
                    traceIds.Enqueue(activity.TraceId.ToString());
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        await using var factory = ApiFactory.CreateProduction();
        using var client = path == "/v1/me" ? factory.CreateClient() : Client(factory);

        using var response = path == "/v1/me"
            ? await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken)
            : await PostAsync(client, path, """{"name":""}""");
        var problem = await ReadProblemAsync(response, (HttpStatusCode)expectedStatus);

        // The bare 32-character trace id, so support can paste it straight into Tempo or Loki.
        Assert.Equal(Assert.Single(traceIds), problem.GetProperty("traceId").GetString());
    }

    private static HttpClient Client(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Platform.CreateOperatorToken());
        return client;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string json) =>
        await client.PostAsync(new Uri(path, UriKind.Relative), new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response, HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var problem = JsonDocument.Parse(body).RootElement.Clone();
        Assert.Equal((int)expectedStatus, problem.GetProperty("status").GetInt32());
        Assert.Matches("^[0-9a-f]{32}$", problem.GetProperty("traceId").GetString());
        Assert.Matches(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$", problem.GetProperty("code").GetString());
        return problem;
    }
}
